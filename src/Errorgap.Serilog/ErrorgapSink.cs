using System;
using System.Collections.Generic;
using Errorgap;
using Serilog.Core;
using Serilog.Events;

namespace Errorgap.Serilog;

/// <summary>
/// Serilog sink. Events at or above <c>notifyLevel</c> (default Error) are
/// reported as errors: the attached exception, or a <see cref="LoggedError"/>
/// carrying the message when there is none. With <c>LogsEnabled</c>, events at
/// or above <c>MinimumLogLevel</c> are also sent to the project's logs.
/// </summary>
public sealed class ErrorgapSink : ILogEventSink
{
    private readonly ErrorgapClient? _client;
    private readonly LogEventLevel _notifyLevel;
    private readonly IFormatProvider? _formatProvider;

    // The SDK's DiagnosticLogger may write to Serilog: an event raised while
    // this sink is sending must not be sent again.
    [ThreadStatic] private static bool _emitting;

    /// <param name="client">The client to send with; defaults to the one
    /// <see cref="ErrorgapSdk.Init"/> created, read at each event.</param>
    public ErrorgapSink(
        ErrorgapClient? client = null,
        LogEventLevel notifyLevel = LogEventLevel.Error,
        IFormatProvider? formatProvider = null)
    {
        _client = client;
        _notifyLevel = notifyLevel;
        _formatProvider = formatProvider;
    }

    public void Emit(LogEvent logEvent)
    {
        if (_emitting) return;
        var client = _client ?? ErrorgapSdk.Client;
        if (client is null) return;

        _emitting = true;
        try
        {
            var message = logEvent.RenderMessage(_formatProvider);
            var component = SourceContext(logEvent);
            if (logEvent.Level >= _notifyLevel)
            {
                Report(client, logEvent, message, component);
            }
            if (client.Config.LogsEnabled
                && !string.IsNullOrWhiteSpace(message)
                && Rank(logEvent.Level) >= MinimumRank(client.Config.MinimumLogLevel))
            {
                client.NotifyLog(message, LevelName(logEvent.Level), component ?? "serilog", logEvent.Timestamp);
            }
        }
        catch (Exception)
        {
            // A sink must never break the code that logged.
        }
        finally
        {
            _emitting = false;
        }
    }

    private static void Report(ErrorgapClient client, LogEvent logEvent, string message, string? component)
    {
        var context = new Dictionary<string, object?>
        {
            ["source"] = "serilog",
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
        foreach (var (name, value) in logEvent.Properties)
        {
            if (name == "SourceContext") continue;
            parameters[name] = value is ScalarValue scalar ? scalar.Value : value.ToString();
        }

        client.Notify(exception, new NoticeOptions { Context = context, Params = parameters });
    }

    private static string? SourceContext(LogEvent logEvent)
        => logEvent.Properties.TryGetValue("SourceContext", out var value) && value is ScalarValue { Value: string name }
            ? name
            : null;

    private static string LevelName(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => "trace",
        LogEventLevel.Debug => "debug",
        LogEventLevel.Information => "info",
        LogEventLevel.Warning => "warning",
        LogEventLevel.Error => "error",
        _ => "fatal",
    };

    // Serilog's levels on the scale of MinimumLogLevel, which takes the
    // Microsoft.Extensions.Logging names (Trace … Critical, None).
    private static int Rank(LogEventLevel level) => (int)level;

    private static int MinimumRank(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "trace" or "verbose" => 0,
        "debug" => 1,
        "information" or "info" => 2,
        "warning" or "warn" => 3,
        "error" => 4,
        "critical" or "fatal" => 5,
        "none" => int.MaxValue,
        _ => 3,
    };
}
