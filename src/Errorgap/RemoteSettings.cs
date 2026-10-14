using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Errorgap;

public enum RemoteKind
{
    Errors,
    Apm,
    Logs,
}

/// <summary>
/// The project's remote settings: what errorgap wants this app to send.
/// Someone can turn errors, performance data or logs off for a project in
/// errorgap (Project settings › What SDKs send) and running apps follow within
/// a poll interval, with no deploy.
///
/// Read from <c>GET /api/projects/:slug/config</c> in the background. Until
/// the first answer, and whenever errorgap cannot be reached, everything stays on.
/// </summary>
public sealed class RemoteSettings
{
    private const int DefaultIntervalSeconds = 600;
    private const int MinIntervalSeconds = 60;

    private readonly Func<ErrorgapConfiguration> _config;
    private readonly HttpClient _http;
    private readonly CancellationToken _shutdown;
    private volatile bool _errorsOff;
    private volatile bool _apmOff;
    private volatile bool _logsOff;
    private int _intervalSeconds = DefaultIntervalSeconds;
    private int _started;

    internal RemoteSettings(Func<ErrorgapConfiguration> config, HttpClient http, CancellationToken shutdown)
    {
        _config = config;
        _http = http;
        _shutdown = shutdown;
    }

    /// <summary>False only when errorgap has said to stop sending this kind of data.</summary>
    public bool Enabled(RemoteKind kind)
    {
        if (!_config().RemoteSettings) return true;
        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            _ = Task.Run(PollAsync);
        }
        return kind switch
        {
            RemoteKind.Apm => !_apmOff,
            RemoteKind.Logs => !_logsOff,
            _ => !_errorsOff,
        };
    }

    /// <summary>Fetch now. Returns whether the settings could be read.</summary>
    public async Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        var config = _config();
        try
        {
            var url = $"{config.Endpoint.TrimEnd('/')}/api/projects/{config.ProjectSlug}/config";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd($"errorgap-dotnet/{Version.Current}");
            if (!string.IsNullOrEmpty(config.ApiKey))
            {
                request.Headers.Add("x-errorgap-project-key", config.ApiKey);
            }
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if ((int)response.StatusCode != 200) return false;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            // An absent field means on.
            _errorsOff = IsFalse(root, "errors_enabled");
            _apmOff = IsFalse(root, "apm_enabled");
            _logsOff = IsFalse(root, "logs_enabled");
            var seconds = root.TryGetProperty("poll_interval_seconds", out var interval) && interval.TryGetInt32(out var parsed)
                ? parsed
                : 0;
            _intervalSeconds = seconds >= MinIntervalSeconds ? seconds : DefaultIntervalSeconds;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsFalse(JsonElement root, string field) =>
        root.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.False;

    private async Task PollAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                await RefreshAsync(_shutdown).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(_intervalSeconds), _shutdown).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }
}
