using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Errorgap;
using Errorgap.Tests;
using Serilog;
using Xunit;

namespace Errorgap.Serilog.Tests;

public class SinkTests
{
    private static ErrorgapConfiguration Config(string endpoint) => new()
    {
        Endpoint = endpoint,
        ProjectSlug = "demo",
        Async = false,
        RemoteSettings = false,
    };

    [Fact]
    public async Task AnErrorWithAnExceptionReportsThatExceptionWithTheMessageAndProperties()
    {
        using var ing = new FakeIngestor();
        await using var client = new ErrorgapClient(Config(ing.Endpoint));
        using var log = new LoggerConfiguration().WriteTo.Errorgap(client).CreateLogger();

        log.ForContext("SourceContext", "Shop.Checkout")
            .Error(new InvalidOperationException("card declined"), "Checkout failed for {OrderId}", 42);

        var notice = Assert.Single(ing.Requests);
        Assert.Equal("/api/projects/demo/notices", notice.Path);
        using var body = JsonDocument.Parse(notice.Raw!);
        var error = body.RootElement.GetProperty("errors")[0];
        Assert.Equal("InvalidOperationException", error.GetProperty("type").GetString());
        Assert.Equal("card declined", error.GetProperty("message").GetString());
        var context = body.RootElement.GetProperty("context");
        Assert.Equal("serilog", context.GetProperty("source").GetString());
        Assert.Equal("Shop.Checkout", context.GetProperty("component").GetString());
        Assert.Equal("error", context.GetProperty("severity").GetString());
        Assert.Equal("Checkout failed for 42", context.GetProperty("log_message").GetString());
        Assert.Equal(42, body.RootElement.GetProperty("params").GetProperty("OrderId").GetInt32());
    }

    [Fact]
    public async Task AnErrorWithoutAnExceptionIsReportedAsALoggedError()
    {
        using var ing = new FakeIngestor();
        await using var client = new ErrorgapClient(Config(ing.Endpoint));
        using var log = new LoggerConfiguration().WriteTo.Errorgap(client).CreateLogger();

        log.Fatal("Disk {Disk} is full", "/var");

        using var body = JsonDocument.Parse(Assert.Single(ing.Requests).Raw!);
        var error = body.RootElement.GetProperty("errors")[0];
        Assert.Equal("LoggedError", error.GetProperty("type").GetString());
        Assert.Equal("Disk \"/var\" is full", error.GetProperty("message").GetString());
        Assert.Equal("fatal", body.RootElement.GetProperty("context").GetProperty("severity").GetString());
    }

    [Fact]
    public async Task BelowTheNotifyLevelNothingIsReportedAndLogsAreOffByDefault()
    {
        using var ing = new FakeIngestor();
        await using var client = new ErrorgapClient(Config(ing.Endpoint));
        using var log = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Errorgap(client).CreateLogger();

        log.Warning("slow");
        log.Information("fine");

        Assert.Empty(ing.Requests);
    }

    [Fact]
    public async Task WithLogsEnabledEventsFromMinimumLogLevelUpAreSentAsLogs()
    {
        using var ing = new FakeIngestor();
        var config = Config(ing.Endpoint);
        config.LogsEnabled = true;
        config.MinimumLogLevel = "Warning";
        await using var client = new ErrorgapClient(config);
        using var log = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Errorgap(client).CreateLogger();

        log.Information("not sent");
        log.ForContext("SourceContext", "Shop.Payments").Warning("Gateway took {Ms} ms", 900);
        log.Error("broken");

        var requests = ing.Requests.ToList();
        var logs = requests.Where(r => r.Path == "/api/projects/demo/logs").ToList();
        Assert.Equal(2, logs.Count);
        Assert.Single(requests, r => r.Path == "/api/projects/demo/notices");

        using var warning = JsonDocument.Parse(logs[0].Raw!);
        Assert.Equal("Gateway took 900 ms", warning.RootElement.GetProperty("message").GetString());
        Assert.Equal("warning", warning.RootElement.GetProperty("level").GetString());
        Assert.Equal("Shop.Payments", warning.RootElement.GetProperty("source").GetString());
        using var error = JsonDocument.Parse(logs[1].Raw!);
        Assert.Equal("error", error.RootElement.GetProperty("level").GetString());
    }

    [Fact]
    public void WithoutAClientEventsAreDroppedQuietly()
    {
        using var log = new LoggerConfiguration().WriteTo.Errorgap().CreateLogger();
        log.Error(new Exception("nowhere to send"), "boom");
    }
}
