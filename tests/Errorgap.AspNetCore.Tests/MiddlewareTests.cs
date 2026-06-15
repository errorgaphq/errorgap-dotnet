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
}
