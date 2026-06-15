using System;
using System.Collections.Generic;

namespace Errorgap;

public sealed class NoticeOptions
{
    public IDictionary<string, object?>? Context { get; set; }
    public IDictionary<string, object?>? Environment { get; set; }
    public IDictionary<string, object?>? Session { get; set; }
    public IDictionary<string, object?>? Params { get; set; }
}

public static class Notice
{
    public const string NotifierId = "errorgap-dotnet";

    public static IDictionary<string, object?> FromException(
        Exception exception,
        ErrorgapConfiguration config,
        NoticeOptions? options = null)
    {
        options ??= new NoticeOptions();

        var defaultContext = new Dictionary<string, object?>
        {
            ["notifier"] = NotifierId,
            ["notifier_version"] = Version.Current,
            ["environment"] = config.Environment,
        };
        if (config.Release is not null) defaultContext["release"] = config.Release;
        if (!string.IsNullOrEmpty(config.RootDirectory)) defaultContext["root_directory"] = config.RootDirectory;

        if (options.Context is not null)
        {
            foreach (var (k, v) in options.Context) defaultContext[k] = v;
        }

        var frames = Backtrace.FromException(exception, config.RootDirectory);
        var frameMaps = new List<IDictionary<string, object?>>(frames.Count);
        foreach (var frame in frames)
        {
            var map = new Dictionary<string, object?>();
            if (frame.File is not null) map["file"] = frame.File;
            if (frame.Line is not null) map["line"] = frame.Line;
            if (frame.Function is not null) map["function"] = frame.Function;
            map["in_app"] = frame.InApp;
            map["index"] = frame.Index;
            frameMaps.Add(map);
        }

        var errorEntry = new Dictionary<string, object?>
        {
            ["type"] = exception.GetType().Name,
            ["message"] = exception.Message ?? string.Empty,
            ["backtrace"] = frameMaps,
        };

        return new Dictionary<string, object?>
        {
            ["project_id"] = config.ProjectId,
            ["received_at"] = DateTime.UtcNow.ToString("o"),
            ["errors"] = new List<IDictionary<string, object?>> { errorEntry },
            ["context"] = defaultContext,
            ["environment"] = options.Environment ?? new Dictionary<string, object?>(),
            ["session"] = options.Session ?? new Dictionary<string, object?>(),
            ["params"] = Filter.Params(options.Params, config.FilterKeys),
        };
    }
}
