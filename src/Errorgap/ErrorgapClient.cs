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
    private readonly Channel<Delivery> _channel;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Task _worker;
    private int _inFlight;
    private ErrorgapConfiguration _config;

    public ErrorgapClient(ErrorgapConfiguration config) : this(config, new HttpClient { Timeout = config.Timeout }) { }

    public ErrorgapClient(ErrorgapConfiguration config, HttpClient http)
    {
        _config = config;
        _http = http;
        _channel = Channel.CreateBounded<Delivery>(new BoundedChannelOptions(config.QueueSize)
        {
            FullMode = BoundedChannelFullMode.Wait,
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
            return Submit(new Delivery(NoticesUrl(), JsonSerializer.Serialize(notice)), sync);
        }
        catch (Exception ex)
        {
            return new DeliveryResult(null, null, ex, false);
        }
    }

    public DeliveryResult NotifyTransaction(ApmTransaction transaction, bool sync = false)
    {
        try
        {
            _config.Validate();
            if (!_config.ApmEnabled
                || _config.ApmSampleRate <= 0
                || (_config.ApmSampleRate < 1 && Random.Shared.NextDouble() >= _config.ApmSampleRate))
            {
                return new DeliveryResult(204, null, null, false);
            }
            return Submit(
                new Delivery(TransactionsUrl(), JsonSerializer.Serialize(transaction.ToPayload(_config))),
                sync);
        }
        catch (Exception ex)
        {
            return new DeliveryResult(null, null, ex, false);
        }
    }

    public DeliveryResult NotifyLog(
        string message,
        string level = "Information",
        string? source = null,
        DateTimeOffset? occurredAt = null,
        bool sync = false)
    {
        try
        {
            _config.Validate();
            if (!_config.LogsEnabled) return new DeliveryResult(204, null, null, false);
            var payload = new Dictionary<string, object?>
            {
                ["message"] = message,
                ["level"] = level.ToLowerInvariant(),
                ["source"] = source,
                ["environment"] = _config.Environment,
                ["occurred_at"] = (occurredAt ?? DateTimeOffset.UtcNow).ToUniversalTime().ToString("o"),
            };
            return Submit(new Delivery(LogsUrl(), JsonSerializer.Serialize(payload)), sync);
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

    private DeliveryResult Submit(Delivery delivery, bool sync)
    {
        if (sync || !_config.Async)
        {
            return DeliverAsync(delivery, _shutdownCts.Token).GetAwaiter().GetResult();
        }
        Interlocked.Increment(ref _inFlight);
        if (!_channel.Writer.TryWrite(delivery))
        {
            Interlocked.Decrement(ref _inFlight);
            return new DeliveryResult(null, null, new InvalidOperationException("queue full"), false);
        }
        return new DeliveryResult(202, null, null, true);
    }

    private async Task<DeliveryResult> DeliverAsync(Delivery delivery, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, delivery.Url)
        {
            Content = new StringContent(delivery.Body, Encoding.UTF8, "application/json"),
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
            if (!response.IsSuccessStatusCode)
            {
                Log($"delivery failed with HTTP {(int)response.StatusCode}: {body}");
            }
            return new DeliveryResult((int)response.StatusCode, body, null, false);
        }
        catch (Exception ex)
        {
            Log($"{ex.GetType().Name}: {ex.Message}");
            return new DeliveryResult(null, null, ex, false);
        }
    }

    private string ProjectUrl(string resource)
        => $"{_config.Endpoint.TrimEnd('/')}/api/projects/{_config.ProjectSlug}/{resource}";

    private string NoticesUrl() => ProjectUrl("notices");
    private string TransactionsUrl() => ProjectUrl("transactions");
    private string LogsUrl() => ProjectUrl("logs");

    private void Log(string message) => _config.DiagnosticLogger?.Invoke($"[errorgap] {message}");

    private sealed record Delivery(string Url, string Body);
}
