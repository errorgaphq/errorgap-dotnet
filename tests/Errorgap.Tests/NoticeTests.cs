using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
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
        var frames = (List<IDictionary<string, object?>>)errors[0]["backtrace"]!;
        Assert.All(frames, frame => Assert.True(frame.ContainsKey("file")));
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

    [Fact]
    public void ShipsSourceExcerptForApplicationFrames()
    {
        var cfg = new ErrorgapConfiguration
        {
            ProjectSlug = "demo",
            RootDirectory = SourceRoot(),
        };
        var notice = Notice.FromException(CapturedException(), cfg);
        var errors = (List<IDictionary<string, object?>>)notice["errors"]!;
        var frames = (List<IDictionary<string, object?>>)errors[0]["backtrace"]!;
        var sourceFrame = frames.First(frame => frame.ContainsKey("source"));
        var source = (IDictionary<string, object?>)sourceFrame["source"]!;
        var lines = (IReadOnlyList<string>)source["lines"]!;

        Assert.Contains(lines, line => line.Contains("source excerpt sentinel"));
        Assert.Equal(true, sourceFrame["in_app"]);
    }

    [Fact]
    public void ShipsSourceExcerptForVendorClassifiedFrames()
    {
        var cfg = new ErrorgapConfiguration
        {
            ProjectSlug = "demo",
            RootDirectory = "/not-the-test-source-root",
        };
        var notice = Notice.FromException(CapturedException(), cfg);
        var errors = (List<IDictionary<string, object?>>)notice["errors"]!;
        var frames = (List<IDictionary<string, object?>>)errors[0]["backtrace"]!;
        var sourceFrame = frames.First(frame => frame.ContainsKey("source"));

        Assert.Equal(false, sourceFrame["in_app"]);
        Assert.NotNull(sourceFrame["source"]);
    }

    [Fact]
    public async System.Threading.Tasks.Task UsesReadableAsyncFunctionNames()
    {
        var cfg = new ErrorgapConfiguration
        {
            ProjectSlug = "demo",
            RootDirectory = SourceRoot(),
        };
        var notice = Notice.FromException(await CapturedAsyncException(), cfg);
        var errors = (List<IDictionary<string, object?>>)notice["errors"]!;
        var frames = (List<IDictionary<string, object?>>)errors[0]["backtrace"]!;

        Assert.Contains(frames, frame => frame["function"]?.ToString()?.EndsWith(".ThrowAsync") == true);
        Assert.DoesNotContain(frames, frame => frame["function"]?.ToString()?.EndsWith(".MoveNext") == true);
    }

    private static Exception CapturedException()
    {
        try
        {
            ThrowFromSource();
        }
        catch (Exception exception)
        {
            return exception;
        }
        throw new InvalidOperationException("unreachable");
    }

    private static async System.Threading.Tasks.Task<Exception> CapturedAsyncException()
    {
        try
        {
            await ThrowAsync();
        }
        catch (Exception exception)
        {
            return exception;
        }
        throw new InvalidOperationException("unreachable");
    }

    private static async System.Threading.Tasks.Task ThrowAsync()
    {
        await System.Threading.Tasks.Task.Yield();
        throw new InvalidOperationException("async source excerpt sentinel");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowFromSource()
    {
        throw new InvalidOperationException("source excerpt sentinel");
    }

    private static string SourceRoot([CallerFilePath] string sourceFile = "")
    {
        return System.IO.Path.GetFullPath(System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(sourceFile)!,
            "../.."));
    }
}
