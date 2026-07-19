using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Errorgap;

namespace Errorgap.AspNetCore;

public sealed class ErrorgapApm : IDisposable
{
    private static readonly AsyncLocal<ScopeState?> Current = new();
    private static readonly Regex QuotedString = new("'(?:''|[^'])*'", RegexOptions.Compiled);
    private static readonly Regex Number = new(@"\b\d+(?:\.\d+)?\b", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    private readonly ErrorgapClient _client;
    private readonly ErrorgapDiagnostics _diagnostics;

    public ErrorgapApm(ErrorgapClient client)
    {
        _client = client;
        _diagnostics = new ErrorgapDiagnostics(this);
    }

    internal TransactionScope BeginTransactionScope()
    {
        var state = new ScopeState(Current.Value);
        Current.Value = state;
        return new TransactionScope(state);
    }

    public void RecordDatabase(string sql, double durationMs)
    {
        var state = Current.Value;
        if (state is null) return;
        var callsite = ApplicationCallsite();
        state.Spans.Add(ApmSpan.Database(
            NormalizeSql(sql),
            durationMs,
            callsite?.File,
            callsite?.Line,
            callsite?.Function));
    }

    public void RecordExternal(double durationMs)
    {
        var state = Current.Value;
        if (state is null || durationMs <= 0) return;
        var callsite = ApplicationCallsite();
        state.Spans.Add(new ApmSpan
        {
            Kind = "ext",
            DurationMs = durationMs,
            File = callsite?.File,
            Line = callsite?.Line,
            Function = callsite?.Function,
        });
    }

    public void TrackJob(string jobClass, string? queue, Action operation)
    {
        TrackJobAsync(jobClass, queue, () =>
        {
            operation();
            return Task.CompletedTask;
        }).GetAwaiter().GetResult();
    }

    public async Task TrackJobAsync(string jobClass, string? queue, Func<Task> operation)
    {
        var stopwatch = Stopwatch.StartNew();
        Exception? failure = null;
        using var scope = BeginTransactionScope();
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (Exception caught)
        {
            failure = caught;
            _client.Notify(caught, new NoticeOptions
            {
                Context = new Dictionary<string, object?>
                {
                    ["source"] = "Errorgap.AspNetCore.ErrorgapApm",
                    ["component"] = "aspnet.job",
                    ["action"] = jobClass,
                },
                Environment = new Dictionary<string, object?>
                {
                    ["queue"] = queue ?? "default",
                },
            }, sync: true);
            throw;
        }
        finally
        {
            stopwatch.Stop();
            _client.NotifyTransaction(new ApmTransaction
            {
                Kind = "job",
                JobClass = jobClass,
                Queue = queue ?? "default",
                StatusCode = failure is null ? 200 : 500,
                DurationMs = stopwatch.Elapsed.TotalMilliseconds,
                Spans = scope.Complete(),
            });
        }
    }

    public static string NormalizeSql(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return string.Empty;
        return Whitespace.Replace(Number.Replace(QuotedString.Replace(sql, "?"), "?"), " ").Trim();
    }

    public void Dispose() => _diagnostics.Dispose();

    private static Callsite? ApplicationCallsite()
    {
        foreach (var frame in new StackTrace(fNeedFileInfo: true).GetFrames() ?? Array.Empty<StackFrame>())
        {
            var method = frame.GetMethod();
            var typeName = method?.DeclaringType?.FullName ?? string.Empty;
            var assemblyName = method?.DeclaringType?.Assembly.GetName().Name;
            if (typeName.StartsWith("System.", StringComparison.Ordinal)
                || typeName.StartsWith("Microsoft.", StringComparison.Ordinal)
                || assemblyName is "Errorgap" or "Errorgap.AspNetCore")
            {
                continue;
            }
            var file = frame.GetFileName();
            if (string.IsNullOrEmpty(file)) continue;
            var line = frame.GetFileLineNumber();
            return new Callsite(
                file.Replace('\\', '/'),
                line > 0 ? line : null,
                FormatFunction(method));
        }
        return null;
    }

    private static string? FormatFunction(System.Reflection.MethodBase? method)
    {
        if (method is null) return null;

        var declaringType = method.DeclaringType;
        var methodName = method.Name;
        if (methodName == "MoveNext" && declaringType?.DeclaringType is { } owner)
        {
            var generatedName = declaringType.Name;
            var start = generatedName.IndexOf('<');
            var end = generatedName.IndexOf('>', start + 1);
            if (start >= 0 && end > start + 1)
            {
                methodName = generatedName.Substring(start + 1, end - start - 1);
                declaringType = owner;
            }
        }

        return declaringType is null ? methodName : $"{declaringType.FullName}.{methodName}";
    }

    internal sealed class ScopeState
    {
        public ScopeState(ScopeState? parent) => Parent = parent;
        public ScopeState? Parent { get; }
        public List<ApmSpan> Spans { get; } = new();
    }

    internal sealed class TransactionScope : IDisposable
    {
        private ScopeState? _state;

        internal TransactionScope(ScopeState state) => _state = state;

        public IReadOnlyList<ApmSpan> Complete()
        {
            var state = Interlocked.Exchange(ref _state, null);
            if (state is null) return Array.Empty<ApmSpan>();
            if (ReferenceEquals(Current.Value, state)) Current.Value = state.Parent;
            return state.Spans.ToArray();
        }

        public void Dispose() => Complete();
    }

    private sealed record Callsite(string File, int? Line, string? Function);
}
