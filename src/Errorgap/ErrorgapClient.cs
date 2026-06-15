using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Errorgap;

public sealed record DeliveryResult(int? Status, string? Body, Exception? Error, bool Queued)
{
    public bool Success => Error is null && Status is >= 200 and < 300;
}

public sealed class ErrorgapClient : IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly Channel<IDictionary<string, object?>> _channel;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Task _worker;
    private int _inFlight;
    private ErrorgapConfiguration _config;

    public ErrorgapClient(ErrorgapConfiguration config) : this(config, new HttpClient { Timeout = config.Timeout }) { }

    public ErrorgapClient(ErrorgapConfiguration config, HttpClient http)
    {
        _config = config;
        _http = http;
        _channel = Channel.CreateBounded<IDictionary<string, object?>>(new BoundedChannelOptions(config.QueueSize)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        _worker = Task.Run(WorkerLoopAsync);
    }

    public ErrorgapConfiguration Config => _config;

    public void Configure(ErrorgapConfiguration config) => _config = config;

    public DeliveryResult Notify(Exception exception, NoticeOptions? options = null, bool sync = false)
    {
        try
        {
            _config.Validate();
            var notice = Notice.FromException(exception, _config, options);

            if (sync || !_config.Async)
            {
                return DeliverAsync(notice, _shutdownCts.Token).GetAwaiter().GetResult();
            }

            if (!_channel.Writer.TryWrite(notice))
            {
                return new DeliveryResult(null, null, new InvalidOperationException("queue full"), false);
            }
            return new DeliveryResult(202, null, null, true);
        }
        catch (Exception ex)
        {
            return new DeliveryResult(null, null, ex, false);
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        while ((_channel.Reader.Count > 0 || Volatile.Read(ref _inFlight) > 0)
               && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await FlushAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        _channel.Writer.TryComplete();
        _shutdownCts.Cancel();
        try { await _worker.ConfigureAwait(false); } catch { /* ignore */ }
        _shutdownCts.Dispose();
        _http.Dispose();
    }

    private async Task WorkerLoopAsync()
    {
        var reader = _channel.Reader;
        try
        {
            while (await reader.WaitToReadAsync(_shutdownCts.Token).ConfigureAwait(false))
            {
                while (reader.TryRead(out var notice))
                {
                    Interlocked.Increment(ref _inFlight);
                    try
                    {
                        await DeliverAsync(notice, _shutdownCts.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _inFlight);
                    }
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    internal async Task<DeliveryResult> DeliverAsync(IDictionary<string, object?> notice, CancellationToken ct)
    {
        var url = $"{_config.Endpoint.TrimEnd('/')}/api/projects/{_config.ProjectSlug}/notices";
        var json = JsonSerializer.Serialize(notice);
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.UserAgent.ParseAdd($"errorgap-dotnet/{Version.Current}");
        if (!string.IsNullOrEmpty(_config.ApiKey))
        {
            request.Headers.Add("x-errorgap-project-key", _config.ApiKey);
        }

        try
        {
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return new DeliveryResult((int)response.StatusCode, body, null, false);
        }
        catch (Exception ex)
        {
            return new DeliveryResult(null, null, ex, false);
        }
    }
}
