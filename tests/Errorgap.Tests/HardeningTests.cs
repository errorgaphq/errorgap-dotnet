using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Errorgap;
using Xunit;

namespace Errorgap.Tests;

public class HardeningTests
{
    private static ErrorgapConfiguration Config(string endpoint) => new()
    {
        Endpoint = endpoint,
        ProjectSlug = "demo",
        Async = false,
    };

    [Fact]
    public async Task A429PausesSendingAndOnlyNetworkAndServerErrorsAreRetried()
    {
        using var ing = new FakeIngestor(429);
        await using var client = new ErrorgapClient(Config(ing.Endpoint));

        Assert.Equal(429, client.Notify(new InvalidOperationException("one")).Status);
        Assert.True(client.Paused);
        Assert.IsType<RateLimitedException>(client.Notify(new InvalidOperationException("two")).Error);
        Assert.Single(ing.Requests);

        Assert.True(ErrorgapClient.Retryable(new DeliveryResult(500, null, null, false)));
        Assert.True(ErrorgapClient.Retryable(new DeliveryResult(null, null, new System.Net.Http.HttpRequestException("refused"), false)));
        Assert.False(ErrorgapClient.Retryable(new DeliveryResult(422, null, null, false)));
        Assert.False(ErrorgapClient.Retryable(new DeliveryResult(429, null, null, false)));
        Assert.False(ErrorgapClient.Retryable(new DeliveryResult(null, null, new RateLimitedException(), false)));
    }

    [Fact]
    public async Task BeforeNotifyCanDropOrChangeANoticeAndAThrowingHookIsSkipped()
    {
        using var ing = new FakeIngestor();
        var drop = Config(ing.Endpoint);
        drop.BeforeNotify = _ => null;
        var change = Config(ing.Endpoint);
        change.BeforeNotify = notice =>
        {
            notice["params"] = new Dictionary<string, object?> { ["team"] = "billing" };
            return notice;
        };
        var broken = Config(ing.Endpoint);
        broken.BeforeNotify = _ => throw new InvalidOperationException("bug in the hook");

        await using (var client = new ErrorgapClient(drop))
        {
            Assert.Equal("dropped by BeforeNotify", client.Notify(new Exception("no")).Body);
        }
        await using (var client = new ErrorgapClient(change))
        {
            Assert.True(client.Notify(new Exception("yes")).Success);
        }
        await using (var client = new ErrorgapClient(broken))
        {
            Assert.True(client.Notify(new Exception("still sent")).Success);
        }

        var raw = ing.Requests.Select(request => request.Raw ?? "").ToArray();
        Assert.Equal(2, raw.Length);
        Assert.Contains("\"team\":\"billing\"", raw[0]);
        Assert.Contains("still sent", raw[1]);
    }

    [Fact]
    public async Task NothingIsSentInAnIgnoredEnvironment()
    {
        using var ing = new FakeIngestor();
        var config = Config(ing.Endpoint);
        config.Environment = "test";
        config.IgnoreEnvironments = new[] { "test" };
        await using var client = new ErrorgapClient(config);

        Assert.Equal("ignored environment", client.Notify(new Exception("x")).Body);
        Assert.Empty(ing.Requests);
    }

    [Fact]
    public void SensitiveKeysAreMaskedInsideListsAndInTheSession()
    {
        var filtered = Filter.Params(
            new Dictionary<string, object?>
            {
                ["users"] = new List<object?> { new Dictionary<string, object?> { ["password"] = "p", ["name"] = "Ada" } },
                ["long"] = new string('x', 10_000),
            },
            ErrorgapConfiguration.DefaultFilterKeys);
        var user = (IDictionary<string, object?>)((List<object?>)filtered["users"]!)[0]!;
        Assert.Equal("[FILTERED]", user["password"]);
        Assert.Equal("Ada", user["name"]);
        Assert.EndsWith("[TRUNCATED]", (string)filtered["long"]!);

        var notice = Notice.FromException(
            new Exception("x"),
            new ErrorgapConfiguration { ProjectSlug = "demo" },
            new NoticeOptions { Session = new Dictionary<string, object?> { ["auth_token"] = "t" } });
        Assert.Equal("[FILTERED]", ((IDictionary<string, object?>)notice["session"]!)["auth_token"]);
    }
}
