using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Errorgap;
using Errorgap.Tests;
using NLog;
using NLog.Config;
using Xunit;

namespace Errorgap.NLog.Tests;

public class TargetTests
{
    private static ErrorgapConfiguration Config(string endpoint) => new()
    {
        Endpoint = endpoint,
        ProjectSlug = "demo",
        Async = false,
        RemoteSettings = false,
    };

    private static LogFactory Factory(ErrorgapTarget target)
    {
        var factory = new LogFactory();
        var config = new LoggingConfiguration(factory);
        config.AddRuleForAllLevels(target);
        factory.Configuration = config;
        return factory;
    }

    [Fact]
    public async Task AnErrorWithAnExceptionReportsThatExceptionWithTheMessageAndProperties()
    {
        using var ing = new FakeIngestor();
        await using var client = new ErrorgapClient(Config(ing.Endpoint));
        using var factory = Factory(new ErrorgapTarget { Client = client });

        factory.GetLogger("Shop.Checkout")
            .Error(new InvalidOperationException("card declined"), "Checkout failed for {OrderId}", 42);

        var notice = Assert.Single(ing.Requests);
        Assert.Equal("/api/projects/demo/notices", notice.Path);
        using var body = JsonDocument.Parse(notice.Raw!);
        var error = body.RootElement.GetProperty("errors")[0];
        Assert.Equal("InvalidOperationException", error.GetProperty("type").GetString());
        Assert.Equal("card declined", error.GetProperty("message").GetString());
        var context = body.RootElement.GetProperty("context");
        Assert.Equal("nlog", context.GetProperty("source").GetString());
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
        using var factory = Factory(new ErrorgapTarget { Client = client });

        factory.GetLogger("Ops").Fatal("Disk {Percent}% full", 97);

        using var body = JsonDocument.Parse(Assert.Single(ing.Requests).Raw!);
        var error = body.RootElement.GetProperty("errors")[0];
        Assert.Equal("LoggedError", error.GetProperty("type").GetString());
        Assert.Equal("Disk 97% full", error.GetProperty("message").GetString());
        Assert.Equal("fatal", body.RootElement.GetProperty("context").GetProperty("severity").GetString());
    }

    [Fact]
    public async Task NotifyLevelIsConfigurableAndLogsAreOffByDefault()
    {
        using var ing = new FakeIngestor();
        await using var client = new ErrorgapClient(Config(ing.Endpoint));
        using var factory = Factory(new ErrorgapTarget { Client = client, NotifyLevel = "Fatal" });
        var log = factory.GetLogger("Ops");

        log.Warn("slow");
        log.Error("not fatal");
        Assert.Empty(ing.Requests);

        log.Fatal("down");
        Assert.Single(ing.Requests);
    }

    [Fact]
    public async Task WithLogsEnabledEventsFromMinimumLogLevelUpAreSentAsLogs()
    {
        using var ing = new FakeIngestor();
        var config = Config(ing.Endpoint);
        config.LogsEnabled = true;
        config.MinimumLogLevel = "Warning";
        await using var client = new ErrorgapClient(config);
        using var factory = Factory(new ErrorgapTarget { Client = client });
        var log = factory.GetLogger("Shop.Payments");

        log.Info("not sent");
        log.Warn("Gateway took {Ms} ms", 900);
        log.Error("broken");

        var requests = ing.Requests.ToList();
        var logs = requests.Where(r => r.Path == "/api/projects/demo/logs").ToList();
        Assert.Equal(2, logs.Count);
        Assert.Single(requests, r => r.Path == "/api/projects/demo/notices");

        using var warning = JsonDocument.Parse(logs[0].Raw!);
        Assert.Equal("Gateway took 900 ms", warning.RootElement.GetProperty("message").GetString());
        Assert.Equal("warning", warning.RootElement.GetProperty("level").GetString());
        Assert.Equal("Shop.Payments", warning.RootElement.GetProperty("source").GetString());
    }

    [Fact]
    public async Task TheTargetLoadsFromXmlAndUsesTheClientFromInit()
    {
        using var ing = new FakeIngestor();
        ErrorgapSdk.Init(Config(ing.Endpoint), captureGlobals: false);
        try
        {
            using var factory = new LogFactory();
            factory.Setup()
                .SetupExtensions(ext => ext.RegisterTarget<ErrorgapTarget>())
                .LoadConfigurationFromXml("""
                    <nlog>
                      <targets><target name="eg" type="Errorgap" notifyLevel="Warn" /></targets>
                      <rules><logger name="*" minlevel="Info" writeTo="eg" /></rules>
                    </nlog>
                    """);

            factory.GetLogger("Jobs").Warn("queue backing up");

            using var body = JsonDocument.Parse(Assert.Single(ing.Requests).Raw!);
            Assert.Equal("queue backing up", body.RootElement.GetProperty("errors")[0].GetProperty("message").GetString());
        }
        finally
        {
            await ErrorgapSdk.ShutdownAsync();
        }
    }
}
