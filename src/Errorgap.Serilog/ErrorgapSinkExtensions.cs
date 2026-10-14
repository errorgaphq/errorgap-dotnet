using System;
using Errorgap;
using Errorgap.Serilog;
using Serilog.Configuration;
using Serilog.Events;

// In the Serilog namespace so `using Serilog;` is enough to find WriteTo.Errorgap().
namespace Serilog;

public static class ErrorgapSinkExtensions
{
    /// <summary>
    /// Send Serilog events to errorgap. Pass <paramref name="client"/> when the
    /// app registers errorgap through dependency injection (ASP.NET Core's
    /// <c>AddErrorgap</c>); otherwise the client from <c>ErrorgapSdk.Init</c> is used.
    /// </summary>
    public static LoggerConfiguration Errorgap(
        this LoggerSinkConfiguration sinkConfiguration,
        ErrorgapClient? client = null,
        LogEventLevel notifyLevel = LogEventLevel.Error,
        LogEventLevel restrictedToMinimumLevel = LevelAlias.Minimum,
        IFormatProvider? formatProvider = null)
        => sinkConfiguration.Sink(new ErrorgapSink(client, notifyLevel, formatProvider), restrictedToMinimumLevel);
}
