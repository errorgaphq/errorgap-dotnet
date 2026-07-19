using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Errorgap;
using Xunit;

namespace Errorgap.Tests;

public class ClientTests
{
    [Fact]
    public async Task PostsToNoticesWithCanonicalHeaders()
    {
        using var ing = new FakeIngestor();
        var cfg = new ErrorgapConfiguration
        {
            Endpoint = ing.Endpoint,
            ProjectSlug = "demo",
            ApiKey = "flk_test",
            Async = false,
        };
        await using var client = new ErrorgapClient(cfg);
        var result = client.Notify(new InvalidOperationException("test"));
        Assert.True(result.Success);

        var reqs = ing.Requests;
        Assert.Single(reqs);
        var first = System.Linq.Enumerable.First(reqs);
        Assert.Equal("POST", first.Method);
        Assert.Equal("/api/projects/demo/notices", first.Path);
        Assert.Equal("flk_test", first.Headers["x-errorgap-project-key"]);
        Assert.StartsWith("errorgap-dotnet/", first.Headers["User-Agent"]);
    }

    [Fact]
    public async Task SendsFullNoticeEnvelope()
    {
        using var ing = new FakeIngestor();
        var cfg = new ErrorgapConfiguration
        {
            Endpoint = ing.Endpoint,
            ProjectSlug = "demo",
            ApiKey = "flk_test",
            Async = false,
        };
        await using var client = new ErrorgapClient(cfg);
        client.Notify(new ArgumentException("kaboom"));

        var first = System.Linq.Enumerable.First(ing.Requests);
        Assert.NotNull(first.Body);
        Assert.True(first.Body!.ContainsKey("errors"));
        Assert.True(first.Body.ContainsKey("context"));
    }

    [Fact]
    public async Task AsyncQueuesAndFlushes()
    {
        using var ing = new FakeIngestor();
        var cfg = new ErrorgapConfiguration
        {
            Endpoint = ing.Endpoint,
            ProjectSlug = "demo",
            ApiKey = "flk_test",
            Async = true,
        };
        await using var client = new ErrorgapClient(cfg);
        for (var index = 0; index < 25; index++)
        {
            var result = client.Notify(new Exception($"x-{index}"));
            Assert.True(result.Queued);
            Assert.Equal(202, result.Status);
        }

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.FlushAsync(cts.Token);
        Assert.Equal(25, ing.Requests.Count);
    }

    [Fact]
    public async Task RejectsMissingProjectSlug()
    {
        using var ing = new FakeIngestor();
        var cfg = new ErrorgapConfiguration { Endpoint = ing.Endpoint, ProjectSlug = null };
        await using var client = new ErrorgapClient(cfg);
        var result = client.Notify(new Exception("x"));
        Assert.NotNull(result.Error);
        Assert.Empty(ing.Requests);
    }

    [Fact]
    public async Task SendsApmTransactionsToCanonicalEndpoint()
    {
        using var ing = new FakeIngestor();
        var cfg = new ErrorgapConfiguration
        {
            Endpoint = ing.Endpoint,
            ProjectSlug = "demo",
            ApiKey = "flk_test",
            Async = false,
            ApmEnabled = true,
        };
        await using var client = new ErrorgapClient(cfg);
        var result = client.NotifyTransaction(new ApmTransaction
        {
            Kind = "web",
            Method = "POST",
            Path = "/orders/{id}",
            PathRaw = "/orders/42",
            StatusCode = 201,
            DurationMs = 12.5,
            Spans = new[] { ApmSpan.Database("SELECT ?", 2.5, "Orders.cs", 42, "Orders.Load") },
        });

        Assert.True(result.Success);
        var request = System.Linq.Enumerable.Single(ing.Requests);
        Assert.Equal("/api/projects/demo/transactions", request.Path);
        var body = request.Body!;
        Assert.Equal("web", body["kind"]?.ToString());
        Assert.Equal("/orders/{id}", body["path"]?.ToString());
    }

    [Fact]
    public async Task SendsStructuredLogsWhenEnabled()
    {
        using var ing = new FakeIngestor();
        var cfg = new ErrorgapConfiguration
        {
            Endpoint = ing.Endpoint,
            ProjectSlug = "demo",
            Async = false,
            LogsEnabled = true,
        };
        await using var client = new ErrorgapClient(cfg);
        var result = client.NotifyLog("payment failed", "Warning", "CheckoutService");

        Assert.True(result.Success);
        var request = System.Linq.Enumerable.Single(ing.Requests);
        Assert.Equal("/api/projects/demo/logs", request.Path);
        Assert.Equal("payment failed", request.Body!["message"]?.ToString());
        Assert.Equal("warning", request.Body["level"]?.ToString());
        Assert.Equal("CheckoutService", request.Body["source"]?.ToString());
    }
}
