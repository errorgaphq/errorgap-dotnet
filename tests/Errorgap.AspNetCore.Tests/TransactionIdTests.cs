using System;
using System.Linq;
using System.Text.Json;
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

/// <summary>Errors reported during a request carry its transaction id.</summary>
public class TransactionIdTests
{
    [Fact]
    public async Task ErrorsRaisedInARequestCarryItsTransactionId()
    {
        using var ing = new FakeIngestor();
        using var host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(svc => svc.AddErrorgap(cfg =>
                {
                    cfg.Endpoint = ing.Endpoint;
                    cfg.ProjectSlug = "demo";
                    cfg.ApiKey = "egp_test";
                    cfg.Async = false;
                    cfg.ApmEnabled = true;
                    cfg.ApmSampleRate = 1.0;
                }));
                web.Configure(app =>
                {
                    app.UseErrorgap();
                    app.Run(async ctx =>
                    {
                        await Task.Yield();
                        ctx.RequestServices.GetRequiredService<ErrorgapClient>()
                            .Notify(new InvalidOperationException("card declined"));
                        throw new InvalidOperationException("boom");
                    });
                });
            })
            .StartAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.GetTestClient().GetAsync("/orders/7"));

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (ing.Requests.Count < 3 && DateTime.UtcNow < deadline) await Task.Delay(50);

        var transaction = ing.Requests.Single(r => r.Path!.EndsWith("/transactions"));
        var id = ((JsonElement)transaction.Body!["id"]!).GetString();
        Assert.Matches("^[0-9a-f-]{36}$", id);
        var notices = ing.Requests.Where(r => r.Path!.EndsWith("/notices")).ToList();
        Assert.Equal(2, notices.Count);
        foreach (var notice in notices)
        {
            var context = (JsonElement)notice.Body!["context"]!;
            Assert.Equal(id, context.GetProperty("transaction_id").GetString());
        }
        Assert.Null(TransactionContext.Current);
    }

    [Fact]
    public async Task ARequestRecordsTheBrowserTraceHeader()
    {
        using var ing = new FakeIngestor();
        using var host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(svc => svc.AddErrorgap(cfg =>
                {
                    cfg.Endpoint = ing.Endpoint;
                    cfg.ProjectSlug = "demo";
                    cfg.ApiKey = "egp_test";
                    cfg.Async = false;
                    cfg.ApmEnabled = true;
                    cfg.ApmSampleRate = 1.0;
                }));
                web.Configure(app =>
                {
                    app.UseErrorgap();
                    app.Run(ctx => Task.CompletedTask);
                });
            })
            .StartAsync();

        var client = host.GetTestClient();
        foreach (var header in new[] { "0192F3C4-7A1B-4C2D-9E3F-0123456789AB", "not-a-uuid" })
        {
            var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, "/orders/7");
            request.Headers.Add("x-errorgap-trace", header);
            await client.SendAsync(request);
        }

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (ing.Requests.Count < 2 && DateTime.UtcNow < deadline) await Task.Delay(50);

        var traces = ing.Requests.Where(r => r.Path!.EndsWith("/transactions"))
            .Select(r => r.Body!.TryGetValue("trace_id", out var v) ? ((JsonElement)v!).GetString() : null)
            .ToList();
        Assert.Equal(new[] { "0192f3c4-7a1b-4c2d-9e3f-0123456789ab", null }, traces);
    }
}
