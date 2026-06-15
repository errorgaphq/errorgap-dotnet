# Errorgap (.NET)

.NET notifier for [Errorgap](https://errorgap.com). Two packages:

- `Errorgap` — base SDK
- `Errorgap.AspNetCore` — ASP.NET Core middleware + DI extensions

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
});

var app = builder.Build();
app.UseErrorgap();   // first, so it sees all downstream exceptions
app.MapGet("/", () => "ok");
app.Run();
```

The middleware reports thrown exceptions then re-throws so the
framework's normal pipeline still handles the response.

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
| `Async` | `true` | Bounded `Channel<>` with drop-oldest |
| `FilterKeys` | `password, token, …` | Substring, case-insensitive |
| `Timeout` | `5 s` | HTTP request timeout |
| `QueueSize` | `100` | |

## Development

```sh
dotnet test
```

## License

MIT.
