using System.Collections.Generic;

namespace Errorgap;

public sealed class ErrorgapConfiguration
{
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

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ProjectSlug))
            throw new System.InvalidOperationException("Errorgap ProjectSlug is required");
        if (string.IsNullOrWhiteSpace(Endpoint))
            throw new System.InvalidOperationException("Errorgap Endpoint is required");
    }
}
