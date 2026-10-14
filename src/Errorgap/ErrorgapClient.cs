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

/// <summary>Not sent: the server asked for a pause (Retry-After) that has not passed yet.</summary>
public sealed class RateLimitedException : Exception
{
    public RateLimitedException() : base("rate limited by the server, not sent") { }
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

    /// <summary>What errorgap wants this app to send (the project's remote settings).</summary>
    public RemoteSettings Remote => _remote ??= new RemoteSettings(() => _config, _http, _shutdownCts.Token);
    private RemoteSettings? _remote;

    public void Configure(ErrorgapConfiguration config) => _config = config;

    public DeliveryResult Notify(Exception exception, NoticeOptions? options = null, bool sync = false)
    {
        try
        {
            _config.Validate();
            if (_config.IgnoredEnvironment())
            {
                return new DeliveryResult(202, "ignored environment", null, false);
            }
            if (!Remote.Enabled(RemoteKind.Errors))
            {
                return new DeliveryResult(202, "errors are switched off for this project", null, false);
            }
            var notice = Notice.FromException(exception, _config, WithTransaction(options));
            // A hook that throws is logged and skipped: a bug in a filter
            // must not lose the error it was filtering.
            if (_config.BeforeNotify is { } hook)
            {
                try
                {
                    var hooked = hook(notice);
                    if (hooked is null)
                    {
                        return new DeliveryResult(202, "dropped by BeforeNotify", null, false);
                    }
                    notice = hooked;
                }
                catch (Exception hookError)
                {
                    Log($"BeforeNotify failed: {hookError.GetType().Name}: {hookError.Message}");
                }
            }
            return Submit(new Delivery(NoticesUrl(), JsonSerializer.Serialize(notice)), sync);
        }
        catch (Exception ex)
        {
            return new DeliveryResult(null, null, ex, false);
        }
    }

    /// <summary>
    /// The request or job this error was raised in (<see cref="TransactionContext"/>),
    /// unless the caller set one, so errorgap links the two.
    /// </summary>
    private static NoticeOptions? WithTransaction(NoticeOptions? options)
    {
        var id = TransactionContext.Current;
        if (id is null) return options;
        if (options?.Context is { } existing && existing.ContainsKey("transaction_id")) return options;
        var context = options?.Context is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(options.Context);
        context["transaction_id"] = id;
        return new NoticeOptions
        {
            Context = context,
            Environment = options?.Environment,
            Session = options?.Session,
            Params = options?.Params,
        };
    }

    public DeliveryResult NotifyTransaction(ApmTransaction transaction, bool sync = false)
    {
        try
        {
            _config.Validate();
            if (!_config.ApmEnabled
                || !Remote.Enabled(RemoteKind.Apm)
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
            if (!_config.LogsEnabled || !Remote.Enabled(RemoteKind.Logs)) return new DeliveryResult(204, null, null, false);
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
                        var result = await DeliverAsync(notice, _shutdownCts.Token).ConfigureAwait(false);
                        if (Retryable(result)) RetryLater(notice);
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

    private const int MaxAttempts = 3;
    private const int BacklogSize = 100;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);
    private long _pausedUntilTicks;
    private int _backlog;

    /// <summary>Whether delivery is paused at the server's request (a 429 or 503 with Retry-After).</summary>
    public bool Paused => DateTime.UtcNow.Ticks < Interlocked.Read(ref _pausedUntilTicks);

    /// <summary>
    /// Network failures and 5xx responses are worth another try. A 4xx is
    /// not: the server understood and refused, and after a 429 the payload is
    /// dropped so a limited project is not sent the same burst again.
    /// </summary>
    public static bool Retryable(DeliveryResult result)
    {
        if (result.Error is RateLimitedException || result.Error is OperationCanceledException) return false;
        if (result.Error is not null) return true;
        return result.Status >= 500;
    }

    private void RetryLater(Delivery delivery)
    {
        if (delivery.Attempt >= MaxAttempts || Volatile.Read(ref _backlog) >= BacklogSize) return;
        Interlocked.Increment(ref _backlog);
        var again = delivery with { Attempt = delivery.Attempt + 1 };
        _ = Task.Delay(RetryDelay * delivery.Attempt, _shutdownCts.Token).ContinueWith(task =>
        {
            Interlocked.Decrement(ref _backlog);
            if (task.IsCanceled) return;
            Interlocked.Increment(ref _inFlight);
            if (!_channel.Writer.TryWrite(again)) Interlocked.Decrement(ref _inFlight);
        }, TaskScheduler.Default);
    }

    private void PauseFor(System.Net.Http.Headers.RetryConditionHeaderValue? retryAfter)
    {
        var seconds = retryAfter?.Delta?.TotalSeconds ?? 60;
        seconds = Math.Max(1, Math.Min(300, seconds));
        Interlocked.Exchange(ref _pausedUntilTicks, DateTime.UtcNow.AddSeconds(seconds).Ticks);
        Log($"rate limited, pausing delivery for {seconds}s");
    }

    private DeliveryResult Submit(Delivery delivery, bool sync)
    {
        if (_config.IgnoredEnvironment())
        {
            return new DeliveryResult(204, null, null, false);
        }
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
        if (Paused)
        {
            return new DeliveryResult(null, null, new RateLimitedException(), false);
        }
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
            if ((int)response.StatusCode is 429 or 503)
            {
                PauseFor(response.Headers.RetryAfter);
            }
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

    private sealed record Delivery(string Url, string Body, int Attempt = 1);
}
