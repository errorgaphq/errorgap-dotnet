using System;
using System.Collections.Generic;
using Errorgap;
using NLog;
using NLog.Targets;

namespace Errorgap.NLog;

/// <summary>
/// NLog target. Events at or above <see cref="NotifyLevel"/> (default Error)
/// are reported as errors: the attached exception, or a <see cref="LoggedError"/>
/// carrying the message when there is none. With <c>LogsEnabled</c>, events at
/// or above <c>MinimumLogLevel</c> are also sent to the project's logs.
/// </summary>
[Target("Errorgap")]
public sealed class ErrorgapTarget : TargetWithLayout
{
    // The SDK's DiagnosticLogger may write to NLog: an event raised while this
    // target is sending must not be sent again.
    [ThreadStatic] private static bool _writing;

    public ErrorgapTarget()
    {
        Name = "errorgap";
        Layout = "${message}";
    }

    /// <summary>The client to send with; defaults to the one
    /// <see cref="ErrorgapSdk.Init"/> created, read at each event.</summary>
    public ErrorgapClient? Client { get; set; }

    /// <summary>The lowest level reported as an error (an NLog level name).</summary>
    public string NotifyLevel { get; set; } = "Error";

    protected override void Write(LogEventInfo logEvent)
    {
        if (_writing) return;
        var client = Client ?? ErrorgapSdk.Client;
        if (client is null) return;

        _writing = true;
        try
        {
            var message = RenderLogEvent(Layout, logEvent);
            var component = string.IsNullOrEmpty(logEvent.LoggerName) ? null : logEvent.LoggerName;
            if (logEvent.Level >= ParseLevel(NotifyLevel, LogLevel.Error))
            {
                Report(client, logEvent, message, component);
            }
            if (client.Config.LogsEnabled
                && !string.IsNullOrWhiteSpace(message)
                && logEvent.Level.Ordinal >= MinimumOrdinal(client.Config.MinimumLogLevel))
            {
                client.NotifyLog(message, LevelName(logEvent.Level), component ?? "nlog", new DateTimeOffset(logEvent.TimeStamp));
            }
        }
        catch (Exception)
        {
            // A target must never break the code that logged.
        }
        finally
        {
            _writing = false;
        }
    }

    private static void Report(ErrorgapClient client, LogEventInfo logEvent, string message, string? component)
    {
        var context = new Dictionary<string, object?>
        {
            ["source"] = "nlog",
            ["component"] = component,
            ["severity"] = LevelName(logEvent.Level),
        };
        var exception = logEvent.Exception;
        if (exception is null)
        {
            exception = new LoggedError(message);
        }
        else
        {
            context["log_message"] = message;
        }

        var parameters = new Dictionary<string, object?>();
        if (logEvent.HasProperties)
        {
            foreach (var (name, value) in logEvent.Properties)
            {
                var key = name?.ToString();
                if (string.IsNullOrEmpty(key)) continue;
                parameters[key] = value is null || value is string || value.GetType().IsPrimitive ? value : value.ToString();
            }
        }

        client.Notify(exception, new NoticeOptions { Context = context, Params = parameters });
    }

    private static LogLevel ParseLevel(string? name, LogLevel fallback)
    {
        try
        {
            return string.IsNullOrWhiteSpace(name) ? fallback : LogLevel.FromString(name.Trim());
        }
        catch (ArgumentException)
        {
            return fallback;
        }
    }

    private static string LevelName(LogLevel level) => level.Ordinal switch
    {
        0 => "trace",
        1 => "debug",
        2 => "info",
        3 => "warning",
        4 => "error",
        _ => "fatal",
    };

    // MinimumLogLevel takes the Microsoft.Extensions.Logging names
    // (Trace … Critical, None); NLog's ordinals line up with them.
    private static int MinimumOrdinal(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "trace" or "verbose" => 0,
        "debug" => 1,
        "information" or "info" => 2,
        "warning" or "warn" => 3,
        "error" => 4,
        "critical" or "fatal" => 5,
        "none" or "off" => int.MaxValue,
        _ => 3,
    };
}
