using System;
using System.Collections.Generic;
using Errorgap;
using Xunit;

namespace Errorgap.Tests;

public class NoticeTests
{
    [Fact]
    public void CapturesTypeAndMessage()
    {
        var cfg = new ErrorgapConfiguration { ProjectSlug = "demo" };
        var notice = Notice.FromException(new InvalidOperationException("boom"), cfg);
        var errors = (List<IDictionary<string, object?>>)notice["errors"]!;
        Assert.Equal("InvalidOperationException", errors[0]["type"]);
        Assert.Equal("boom", errors[0]["message"]);
    }

    [Fact]
    public void IncludesNotifierIdentification()
    {
        var cfg = new ErrorgapConfiguration { ProjectSlug = "demo", Environment = "test", Release = "1.2.3" };
        var notice = Notice.FromException(new Exception("x"), cfg);
        var ctx = (IDictionary<string, object?>)notice["context"]!;
        Assert.Equal("errorgap-dotnet", ctx["notifier"]);
        Assert.Equal(Errorgap.Version.Current, ctx["notifier_version"]);
        Assert.Equal("test", ctx["environment"]);
        Assert.Equal("1.2.3", ctx["release"]);
    }

    [Fact]
    public void FiltersSensitiveParams()
    {
        var cfg = new ErrorgapConfiguration { ProjectSlug = "demo" };
        var options = new NoticeOptions
        {
            Params = new Dictionary<string, object?>
            {
                ["username"] = "alice",
                ["password"] = "hunter2",
            },
        };
        var notice = Notice.FromException(new Exception("x"), cfg, options);
        var p = (IDictionary<string, object?>)notice["params"]!;
        Assert.Equal("[FILTERED]", p["password"]);
        Assert.Equal("alice", p["username"]);
    }
}
