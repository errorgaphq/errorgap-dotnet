# Errorgap for .NET

.NET error monitoring, APM, and log collection for [Errorgap](https://errorgap.com). Two packages:

- `Errorgap` — error reporting plus low-level transaction and log delivery
- `Errorgap.AspNetCore` — ASP.NET Core middleware, APM instrumentation, and `ILogger` integration

Requires .NET 8+.

## Install

```sh
dotnet add package Errorgap.AspNetCore
```

For non-web apps, depend on `Errorgap` directly.

## ASP.NET Core

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddErrorgap(cfg =>
{
    cfg.Endpoint    = builder.Configuration["ERRORGAP_ENDPOINT"]!;
    cfg.ProjectSlug = builder.Configuration["ERRORGAP_PROJECT_SLUG"]!;
    cfg.ApiKey      = builder.Configuration["ERRORGAP_API_KEY"];
    cfg.ApmEnabled  = true;
    cfg.LogsEnabled = true;
});

var app = builder.Build();
app.UseErrorgap();   // first, so it sees all downstream exceptions
app.MapGet("/", () => "ok");
app.Run();
```

The middleware reports thrown exceptions, request context, filtered query/form
parameters, normalized route patterns, and web transaction timings. It then
re-throws so the framework's normal pipeline still handles the response.

When portable PDB file information points to source available in the deployed
application, Errorgap includes a bounded source excerpt for application and
vendor frames. Set `RootDirectory` to the application source root to classify
application frames and display their paths relative to that root.

### APM

With `ApmEnabled`, ASP.NET Core requests are recorded automatically. Diagnostic
listeners add Entity Framework Core/SqlClient query spans and outbound HTTP
spans to the current transaction. SQL literals are normalized before delivery.

Wrap background work to report job timings, failures, and any spans created
during the operation:

```csharp
await apm.TrackJobAsync("ReceiptJob", "receipts", () => job.RunAsync());
```

Set `ApmSampleRate` between `0` and `1` to sample transactions.

### Logs

With `LogsEnabled`, the ASP.NET Core package registers an `ILoggerProvider`.
Messages at or above `MinimumLogLevel` are sent with their category, level, and
environment:

```csharp
logger.LogWarning("Payment gateway timeout for {OrderId}", orderId);
```

## Plain .NET

```csharp
using Errorgap;

ErrorgapSdk.Init(new ErrorgapConfiguration
{
    Endpoint = Environment.GetEnvironmentVariable("ERRORGAP_ENDPOINT")!,
    ProjectSlug = Environment.GetEnvironmentVariable("ERRORGAP_PROJECT_SLUG")!,
    ApiKey = Environment.GetEnvironmentVariable("ERRORGAP_API_KEY"),
});

try { Risky(); }
catch (Exception ex)
{
    ErrorgapSdk.Notify(ex);
    throw;
}

await ErrorgapSdk.FlushAsync();
await ErrorgapSdk.ShutdownAsync();
```

`Init` installs `AppDomain.UnhandledException` and
`TaskScheduler.UnobservedTaskException` hooks by default.

## Configuration reference

| Property | Default | Notes |
|---|---|---|
| `Endpoint` | `ERRORGAP_ENDPOINT` or `http://127.0.0.1:3030` | |
| `ProjectSlug` | `ERRORGAP_PROJECT_SLUG` | **Required** |
| `ProjectId` | `ERRORGAP_PROJECT_ID` | |
| `ApiKey` | `ERRORGAP_API_KEY` | Sent as `x-errorgap-project-key` |
| `Environment` | `ERRORGAP_ENVIRONMENT` or `production` | |
| `Release` | — | |
| `Async` | `true` | Uses a bounded background delivery queue |
| `FilterKeys` | `password, token, …` | Substring, case-insensitive |
| `Timeout` | `5 s` | HTTP request timeout |
| `QueueSize` | `100` | |
| `RootDirectory` | Current directory | Source lookup and app-frame classification |
| `ApmEnabled` | `ERRORGAP_APM_ENABLED` or `false` | Automatic ASP.NET Core APM |
| `ApmSampleRate` | `ERRORGAP_APM_SAMPLE_RATE` or `1` | Clamped to `0`–`1` |
| `LogsEnabled` | `ERRORGAP_LOGS_ENABLED` or `false` | Enables `ILogger` delivery |
| `MinimumLogLevel` | `ERRORGAP_MINIMUM_LOG_LEVEL` or `Warning` | Case-insensitive .NET log level |
| `DiagnosticLogger` | — | Optional callback for delivery failures |

## Development

```sh
dotnet test
```

## License

MIT.
