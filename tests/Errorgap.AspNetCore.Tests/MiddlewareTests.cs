using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Errorgap;
using Errorgap.AspNetCore;
using Errorgap.Tests;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Errorgap.AspNetCore.Tests;

public class MiddlewareTests
{
    [Fact]
    public async Task ReportsThrownExceptionsWithRequestContext()
    {
        using var ing = new FakeIngestor();

        using var host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(svc =>
                {
                    svc.AddErrorgap(cfg =>
                    {
                        cfg.Endpoint = ing.Endpoint;
                        cfg.ProjectSlug = "demo";
                        cfg.ApiKey = "flk_test";
                        cfg.Async = false;
                    });
                });
                web.Configure(app =>
                {
                    app.UseErrorgap();
                    app.Run(_ => throw new InvalidOperationException("boom"));
                });
            })
            .StartAsync();

        var client = host.GetTestClient();
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("/boom?x=1"));

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (ing.Requests.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.Single(ing.Requests);
        var first = System.Linq.Enumerable.First(ing.Requests);
        Assert.NotNull(first.Body);
        var errors = (System.Text.Json.JsonElement)first.Body!["errors"]!;
        var first0 = errors[0];
        Assert.Equal("boom", first0.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ReportsNormalizedRoutesRedactedParamsAndApm()
    {
        using var ing = new FakeIngestor();
        using var host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddErrorgap(cfg =>
                    {
                        cfg.Endpoint = ing.Endpoint;
                        cfg.ProjectSlug = "demo";
                        cfg.Async = false;
                        cfg.ApmEnabled = true;
                    });
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseErrorgap();
                    app.UseEndpoints(endpoints => endpoints.MapGet(
                        "/orders/{orderId}",
                        _ => throw new InvalidOperationException("route boom")));
                });
            })
            .StartAsync();

        var client = host.GetTestClient();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GetAsync("/orders/42?customer_id=cus_123&password=secret-value"));

        var requests = ing.Requests.ToArray();
        Assert.Equal(2, requests.Length);
        var notice = System.Linq.Enumerable.Single(requests, request => request.Path?.EndsWith("/notices") == true);
        var transaction = System.Linq.Enumerable.Single(requests, request => request.Path?.EndsWith("/transactions") == true);
        var context = (System.Text.Json.JsonElement)notice.Body!["context"]!;
        var parameters = (System.Text.Json.JsonElement)notice.Body["params"]!;
        Assert.Equal("/orders/{orderId}", context.GetProperty("component").GetString());
        Assert.Equal("cus_123", parameters.GetProperty("query").GetProperty("customer_id").GetString());
        Assert.Equal("[FILTERED]", parameters.GetProperty("query").GetProperty("password").GetString());
        Assert.Equal("/orders/{orderId}", transaction.Body!["path"]?.ToString());
        Assert.Equal("500", transaction.Body["status_code"]?.ToString());
    }

    [Fact]
    public async Task TracksFailedJobsWithDatabaseSpans()
    {
        using var ing = new FakeIngestor();
        var configuration = new ErrorgapConfiguration
        {
            Endpoint = ing.Endpoint,
            ProjectSlug = "demo",
            Async = false,
            ApmEnabled = true,
        };
        await using var client = new ErrorgapClient(configuration);
        using var apm = new ErrorgapApm(client);

        Assert.Throws<InvalidOperationException>(() => apm.TrackJob(
            "ReceiptJob",
            "receipts",
            () =>
            {
                apm.RecordDatabase("SELECT 7 WHERE id = 42", 4.5);
                apm.RecordExternal(2.25);
                throw new InvalidOperationException("job failed");
            }));

        var requests = ing.Requests.ToArray();
        Assert.Equal(2, requests.Length);
        var transaction = System.Linq.Enumerable.Single(requests, request => request.Path?.EndsWith("/transactions") == true);
        Assert.Equal("job", transaction.Body!["kind"]?.ToString());
        Assert.Equal("ReceiptJob", transaction.Body["job_class"]?.ToString());
        var spans = (System.Text.Json.JsonElement)transaction.Body["spans"]!;
        Assert.Equal("SELECT ? WHERE id = ?", spans[0].GetProperty("sql").GetString());
        Assert.Equal("ext", spans[1].GetProperty("kind").GetString());
        Assert.Equal(2.25, spans[1].GetProperty("duration_ms").GetDouble());
    }

    [Fact]
    public async Task LoggerProviderForwardsConfiguredLevels()
    {
        using var ing = new FakeIngestor();
        var configuration = new ErrorgapConfiguration
        {
            Endpoint = ing.Endpoint,
            ProjectSlug = "demo",
            Async = false,
            LogsEnabled = true,
            MinimumLogLevel = "Warning",
        };
        await using var client = new ErrorgapClient(configuration);
        using var provider = new ErrorgapLoggerProvider(client, configuration);
        var logger = provider.CreateLogger("Errorgap.SampleApp.CheckoutService");

        logger.LogInformation("ignored");
        logger.LogWarning("payment gateway timeout");

        var request = System.Linq.Enumerable.Single(ing.Requests);
        Assert.Equal("/api/projects/demo/logs", request.Path);
        Assert.Equal("payment gateway timeout", request.Body!["message"]?.ToString());
        Assert.Equal("Errorgap.SampleApp.CheckoutService", request.Body["source"]?.ToString());
    }
}
