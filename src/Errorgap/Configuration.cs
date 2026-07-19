using System.Collections.Generic;

namespace Errorgap;

public sealed class ErrorgapConfiguration
{
    private double _apmSampleRate = ParseSampleRate(
        System.Environment.GetEnvironmentVariable("ERRORGAP_APM_SAMPLE_RATE") ?? "1");

    public static readonly IReadOnlyList<string> DefaultFilterKeys = new[]
    {
        "password", "password_confirmation", "token", "secret",
        "api_key", "authorization", "cookie",
    };

    public string Endpoint { get; set; } =
        System.Environment.GetEnvironmentVariable("ERRORGAP_ENDPOINT") ?? "http://127.0.0.1:3030";

    public string? ProjectSlug { get; set; } =
        System.Environment.GetEnvironmentVariable("ERRORGAP_PROJECT_SLUG");

    public string? ProjectId { get; set; } =
        System.Environment.GetEnvironmentVariable("ERRORGAP_PROJECT_ID");

    public string? ApiKey { get; set; } =
        System.Environment.GetEnvironmentVariable("ERRORGAP_API_KEY");

    public string Environment { get; set; } =
        System.Environment.GetEnvironmentVariable("ERRORGAP_ENVIRONMENT") ?? "production";

    public string? Release { get; set; }

    public bool Async { get; set; } = true;

    public IReadOnlyList<string> FilterKeys { get; set; } = DefaultFilterKeys;

    public System.TimeSpan Timeout { get; set; } = System.TimeSpan.FromSeconds(5);

    public int QueueSize { get; set; } = 100;

    public string RootDirectory { get; set; } = System.IO.Directory.GetCurrentDirectory();

    public bool ApmEnabled { get; set; } = ParseBoolean("ERRORGAP_APM_ENABLED", false);

    public double ApmSampleRate
    {
        get => _apmSampleRate;
        set => _apmSampleRate = System.Math.Clamp(value, 0, 1);
    }

    public bool LogsEnabled { get; set; } = ParseBoolean("ERRORGAP_LOGS_ENABLED", false);

    public string MinimumLogLevel { get; set; } =
        System.Environment.GetEnvironmentVariable("ERRORGAP_MINIMUM_LOG_LEVEL") ?? "Warning";

    public System.Action<string>? DiagnosticLogger { get; set; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ProjectSlug))
            throw new System.InvalidOperationException("Errorgap ProjectSlug is required");
        if (string.IsNullOrWhiteSpace(Endpoint))
            throw new System.InvalidOperationException("Errorgap Endpoint is required");
    }

    private static bool ParseBoolean(string key, bool fallback)
    {
        var value = System.Environment.GetEnvironmentVariable(key);
        return bool.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private static double ParseSampleRate(string value)
    {
        return double.TryParse(
            value,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed)
            ? System.Math.Clamp(parsed, 0, 1)
            : 1;
    }
}
