using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace Errorgap;

public static class Backtrace
{
    private const int ContextRadius = 6;
    private const long MaxSourceBytes = 2 * 1024 * 1024;
    private const int MaxLineLength = 400;

    public sealed record Source(int StartLine, IReadOnlyList<string> Lines);

    public sealed record Frame(
        string? File,
        int? Line,
        string? Function,
        bool InApp,
        int Index,
        Source? Source);

    public static List<Frame> FromException(Exception exception, string rootDirectory)
    {
        var frames = new List<Frame>();
        var index = 0;
        Exception? current = exception;
        while (current != null)
        {
            var trace = new StackTrace(current, fNeedFileInfo: true);
            foreach (var raw in trace.GetFrames() ?? Array.Empty<StackFrame>())
            {
                if (raw is null) continue;
                var method = raw.GetMethod();
                var rawFile = raw.GetFileName();
                int? line = raw.GetFileLineNumber() > 0 ? raw.GetFileLineNumber() : null;
                var inApp = IsInApp(method, rawFile, rootDirectory);
                frames.Add(new Frame(
                    File: DisplayPath(rawFile, rootDirectory, inApp),
                    Line: line,
                    Function: FormatFunction(method),
                    InApp: inApp,
                    Index: index++,
                    Source: line is null ? null : SourceFor(rawFile, line.Value)
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
        var methodName = method.Name;
        if (methodName == "MoveNext" && type?.DeclaringType is { } owner)
        {
            var generatedName = type.Name;
            var start = generatedName.IndexOf('<');
            var end = generatedName.IndexOf('>', start + 1);
            if (start >= 0 && end > start + 1)
            {
                methodName = generatedName.Substring(start + 1, end - start - 1);
                type = owner;
            }
        }
        return type is null ? methodName : $"{type.FullName}.{methodName}";
    }

    private static bool IsInApp(MethodBase? method, string? file, string rootDirectory)
    {
        if (method?.DeclaringType?.FullName is { } name)
        {
            var assemblyName = method.DeclaringType.Assembly.GetName().Name;
            if (name.StartsWith("System.", StringComparison.Ordinal)
                || name.StartsWith("Microsoft.", StringComparison.Ordinal)
                || name.StartsWith("Xunit.", StringComparison.Ordinal)
                || name.StartsWith("JetBrains.", StringComparison.Ordinal)
                || assemblyName is "Errorgap" or "Errorgap.AspNetCore")
                return false;
        }
        if (string.IsNullOrEmpty(file)) return false;
        if (string.IsNullOrEmpty(rootDirectory)) return false;
        try
        {
            var fullFile = Path.GetFullPath(file);
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return fullFile.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison);
        }
        catch (Exception) when (file is not null)
        {
            return false;
        }
    }

    private static string? DisplayPath(string? file, string rootDirectory, bool inApp)
    {
        if (string.IsNullOrEmpty(file) || !inApp || string.IsNullOrEmpty(rootDirectory))
            return file;
        try
        {
            return Path.GetRelativePath(rootDirectory, file).Replace('\\', '/');
        }
        catch (Exception)
        {
            return file;
        }
    }

    private static Source? SourceFor(string? file, int targetLine)
    {
        if (string.IsNullOrWhiteSpace(file)) return null;
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Length > MaxSourceBytes) return null;
            var lines = File.ReadAllLines(file);
            if (lines.Length == 0) return null;

            var targetIndex = Math.Clamp(targetLine - 1, 0, lines.Length - 1);
            var startIndex = Math.Max(0, targetIndex - ContextRadius);
            var endIndex = Math.Min(lines.Length, targetIndex + ContextRadius + 1);
            var excerpt = new List<string>(endIndex - startIndex);
            for (var i = startIndex; i < endIndex; i++)
            {
                var line = lines[i];
                excerpt.Add(line.Length > MaxLineLength ? line[..MaxLineLength] : line);
            }
            return new Source(startIndex + 1, excerpt);
        }
        catch (Exception) when (file is not null)
        {
            return null;
        }
    }
}
