# Errorgap for .NET

.NET error monitoring, APM, and log collection for [Errorgap](https://errorgap.com). Four packages:

- `Errorgap` — error reporting plus low-level transaction and log delivery
- `Errorgap.AspNetCore` — ASP.NET Core middleware, APM instrumentation, and `ILogger` integration
- `Errorgap.Serilog` — Serilog sink
- `Errorgap.NLog` — NLog target

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

### Link errors to their request

`UseErrorgap()` and `ErrorgapApm.TrackJobAsync` give each request or job a
transaction id. Errors reported while it runs — the middleware's own, or your
`ErrorgapClient.Notify` calls — carry it as `context.transaction_id`, so
errorgap shows the error a request actually raised on its trace. The id flows
across awaits (`AsyncLocal`). For your own instrumentation:

```csharp
var transaction = new ApmTransaction { Method = "GET", Path = "/orders/{id}" };
using (TransactionContext.Enter(transaction.Id))
{
    await HandleAsync(request);
}
client.NotifyTransaction(transaction);
```

### Link browser calls to their request

When the errorgap browser SDK is on the page, its API calls send an
`x-errorgap-trace` header. `UseErrorgap()` records it as the transaction's
`TraceId` (for your own instrumentation: `ApmTransaction.BrowserTraceId(header)`),
so errorgap's browser Performance view links each call to the server request
that answered it.

### Logs

With `LogsEnabled`, the ASP.NET Core package registers an `ILoggerProvider`.
Messages at or above `MinimumLogLevel` are sent with their category, level, and
environment:

```csharp
logger.LogWarning("Payment gateway timeout for {OrderId}", orderId);
```

## Serilog

```sh
dotnet add package Errorgap.Serilog
```

```csharp
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Errorgap()
    .CreateLogger();
```

Error and Fatal events are reported as errors. `Log.Error(ex, "…")` reports
`ex`, with the rendered message in the notice's context; an event with no
exception is reported as a `LoggedError` carrying the message. The event's
properties go in the notice's params, and its `SourceContext` is the component.
Pass `notifyLevel: LogEventLevel.Warning` to report warnings too.

With `LogsEnabled`, events at or above `MinimumLogLevel` are also sent to the
project's logs. This matters when Serilog replaces the built-in logging
(`UseSerilog()`), because the `ILogger` provider from `Errorgap.AspNetCore`
no longer sees anything then.

`WriteTo.Errorgap()` uses the client from `ErrorgapSdk.Init`. With ASP.NET
Core's `AddErrorgap`, pass the registered client instead:

```csharp
builder.Services.AddErrorgap();
builder.Services.AddSerilog((services, lc) => lc
    .WriteTo.Console()
    .WriteTo.Errorgap(services.GetRequiredService<ErrorgapClient>()));
```

## NLog

```sh
dotnet add package Errorgap.NLog
```

Register the target, then use it from `nlog.config`:

```csharp
LogManager.Setup().SetupExtensions(ext => ext.RegisterTarget<Errorgap.NLog.ErrorgapTarget>());
```

```xml
<nlog>
  <targets>
    <target name="errorgap" type="Errorgap" />
  </targets>
  <rules>
    <logger name="*" minlevel="Info" writeTo="errorgap" />
  </rules>
</nlog>
```

or, without the XML, in code:

```csharp
LogManager.Setup().LoadConfiguration(c =>
    c.ForLogger().FilterMinLevel(NLog.LogLevel.Info).WriteTo(new ErrorgapTarget()));
```

Events behave as with Serilog: Error and Fatal are reported as errors (set
`notifyLevel="Warn"` to include warnings), with the logger name as the
component and the event's properties as params, and with `LogsEnabled`, events
at or above `MinimumLogLevel` also go to the project's logs. The target uses
the client from `ErrorgapSdk.Init`; set its `Client` property to use another.

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

## Remote settings

Errors, performance data and logs can each be switched off for a project in
errorgap (Project settings › What SDKs send). Running apps follow within ten
minutes, with no deploy: the client re-reads `GET /api/projects/:slug/config`
in the background. If errorgap cannot be reached, everything stays on.
`RemoteSettings = false` (or `ERRORGAP_REMOTE_SETTINGS=false`) ignores the
project's switches.

## Filtering, hooks and delivery

```csharp
var config = new ErrorgapConfiguration
{
    ProjectSlug = "my-app",
    IgnoreEnvironments = new[] { "Testing" },
    // return null to drop the notice, or return it changed
    BeforeNotify = notice => notice,
};
```

`FilterKeys` are masked at any depth in `Params` and `Session`, inside lists
too, and oversized values are cut. A send that fails on the network or with a
5xx is retried up to three times; after a 429 the client stops sending for the
server's `Retry-After` (`client.Paused`).

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
| `LogsEnabled` | `ERRORGAP_LOGS_ENABLED` or `false` | Enables log delivery (`ILogger`, Serilog, NLog) |
| `MinimumLogLevel` | `ERRORGAP_MINIMUM_LOG_LEVEL` or `Warning` | Case-insensitive .NET log level |
| `DiagnosticLogger` | — | Optional callback for delivery failures |

## Development

```sh
dotnet test
```

## License

MIT.
