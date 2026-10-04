using System;
using System.Collections.Generic;
using System.Linq;

namespace Errorgap;

public sealed class ApmTransaction
{
    /// <summary>Links errors raised during this transaction to it; see <see cref="TransactionContext"/>.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();
    /// <summary>The browser's x-errorgap-trace header; see <see cref="BrowserTraceId"/>.</summary>
    public string? TraceId { get; set; }
    public string Kind { get; set; } = "web";
    public string? Method { get; set; }
    public string? Path { get; set; }
    public string? PathRaw { get; set; }
    public int? StatusCode { get; set; }
    public double DurationMs { get; set; }
    public string? Environment { get; set; }
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
    public IReadOnlyList<ApmSpan> Spans { get; set; } = Array.Empty<ApmSpan>();
    public string? JobClass { get; set; }
    public string? Queue { get; set; }

    /// <summary>The header the errorgap browser SDK sends with API calls.</summary>
    public const string TraceHeader = "x-errorgap-trace";

    /// <summary>
    /// The trace id in an x-errorgap-trace header value, lowercased, or null
    /// unless it is a well-formed UUID. Errorgap links the browser's view of an
    /// API call to the transaction that carries it.
    /// </summary>
    public static string? BrowserTraceId(string? header)
    {
        var value = header?.Trim().ToLowerInvariant();
        return value is { Length: 36 } && Guid.TryParseExact(value, "D", out _) ? value : null;
    }

    internal IDictionary<string, object?> ToPayload(ErrorgapConfiguration configuration)
    {
        var payload = new Dictionary<string, object?>
        {
            ["id"] = Id,
            ["kind"] = Kind,
            ["duration_ms"] = DurationMs,
            ["environment"] = Environment ?? configuration.Environment,
            ["occurred_at"] = OccurredAt.ToUniversalTime().ToString("o"),
            ["spans"] = Spans.Select(span => span.ToPayload()).ToArray(),
        };
        if (TraceId is not null) payload["trace_id"] = TraceId;
        if (Method is not null) payload["method"] = Method;
        if (Path is not null) payload["path"] = Path;
        if (PathRaw is not null) payload["path_raw"] = PathRaw;
        if (StatusCode is not null) payload["status_code"] = StatusCode;
        if (JobClass is not null) payload["job_class"] = JobClass;
        if (Queue is not null) payload["queue"] = Queue;
        return payload;
    }
}
