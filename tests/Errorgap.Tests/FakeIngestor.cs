using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;

namespace Errorgap.Tests;

public sealed class CapturedRequest
{
    public string? Path { get; init; }
    public string? Method { get; init; }
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, object?>? Body { get; init; }
    public string? Raw { get; init; }
}

public sealed class FakeIngestor : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly ConcurrentQueue<CapturedRequest> _requests = new();
    private readonly int _status;
    private readonly Task _loop;
    private volatile bool _running = true;

    public FakeIngestor(int status = 201)
    {
        _status = status;
        var port = GetFreePort();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        Endpoint = $"http://127.0.0.1:{port}";
        _loop = Task.Run(LoopAsync);
    }

    public string Endpoint { get; }

    /// <summary>The JSON served to the SDK's settings poll; null answers 404.</summary>
    public volatile string? Settings;
    public IReadOnlyCollection<CapturedRequest> Requests => _requests.ToArray();

    private async Task LoopAsync()
    {
        while (_running)
        {
            HttpListenerContext? ctx = null;
            try { ctx = await _listener.GetContextAsync(); }
            catch { return; }

            // The SDK's settings poll is not an ingest request: answered from
            // Settings (404 when unset) and not recorded.
            if (ctx.Request.HttpMethod == "GET")
            {
                var settings = Settings;
                ctx.Response.StatusCode = settings is null ? 404 : 200;
                if (settings is not null)
                {
                    await ctx.Response.OutputStream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(settings));
                }
                ctx.Response.Close();
                continue;
            }

            using var reader = new StreamReader(ctx.Request.InputStream);
            var raw = await reader.ReadToEndAsync();
            Dictionary<string, object?>? body = null;
            try { body = JsonSerializer.Deserialize<Dictionary<string, object?>>(raw); }
            catch { /* leave null */ }

            var captured = new CapturedRequest
            {
                Path = ctx.Request.Url?.AbsolutePath,
                Method = ctx.Request.HttpMethod,
                Body = body,
                Raw = raw,
            };
            foreach (string key in ctx.Request.Headers)
            {
                var v = ctx.Request.Headers[key];
                if (v is not null) captured.Headers[key] = v;
            }
            _requests.Enqueue(captured);

            ctx.Response.StatusCode = _status;
            ctx.Response.ContentType = "application/json";
            var bytes = System.Text.Encoding.UTF8.GetBytes("{\"group_id\":\"g_1\"}");
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    public void Dispose()
    {
        _running = false;
        try { _listener.Stop(); } catch { }
        try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _listener.Close();
    }

    private static int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
