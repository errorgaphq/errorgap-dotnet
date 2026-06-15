using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;

namespace Errorgap;

public static class Backtrace
{
    public sealed record Frame(string? File, int? Line, string? Function, bool InApp, int Index);

    public static List<Frame> FromException(Exception exception, string rootDirectory)
    {
        var frames = new List<Frame>();
        var index = 0;
        Exception? current = exception;
        while (current != null)
        {
            var trace = new StackTrace(current, fNeedFileInfo: true);
            foreach (var raw in trace.GetFrames())
            {
                if (raw is null) continue;
                var method = raw.GetMethod();
                frames.Add(new Frame(
                    File: raw.GetFileName(),
                    Line: raw.GetFileLineNumber() > 0 ? raw.GetFileLineNumber() : null,
                    Function: FormatFunction(method),
                    InApp: IsInApp(method, raw.GetFileName(), rootDirectory),
                    Index: index++
                ));
            }
            current = current.InnerException;
        }
        return frames;
    }

    private static string? FormatFunction(MethodBase? method)
    {
        if (method is null) return null;
        var type = method.DeclaringType;
        return type is null
            ? method.Name
            : $"{type.FullName}.{method.Name}";
    }

    private static bool IsInApp(MethodBase? method, string? file, string rootDirectory)
    {
        if (method?.DeclaringType?.FullName is { } name)
        {
            if (name.StartsWith("System.") || name.StartsWith("Microsoft.")
                || name.StartsWith("Xunit.") || name.StartsWith("JetBrains."))
                return false;
        }
        if (string.IsNullOrEmpty(file)) return false;
        if (string.IsNullOrEmpty(rootDirectory)) return false;
        return file.StartsWith(rootDirectory, StringComparison.Ordinal);
    }
}
