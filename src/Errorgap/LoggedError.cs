using System;

namespace Errorgap;

/// <summary>
/// An error-level log message that had no exception attached, reported by the
/// logging integrations (Errorgap.Serilog, Errorgap.NLog) so it still shows up
/// as an error, grouped by its message.
/// </summary>
public sealed class LoggedError : Exception
{
    public LoggedError(string message) : base(message) { }
}
