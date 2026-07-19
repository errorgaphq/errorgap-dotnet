using System;
using System.Collections.Generic;
using System.Linq;

namespace Errorgap;

public sealed class ApmTransaction
{
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

    internal IDictionary<string, object?> ToPayload(ErrorgapConfiguration configuration)
    {
        var payload = new Dictionary<string, object?>
        {
            ["kind"] = Kind,
            ["duration_ms"] = DurationMs,
            ["environment"] = Environment ?? configuration.Environment,
            ["occurred_at"] = OccurredAt.ToUniversalTime().ToString("o"),
            ["spans"] = Spans.Select(span => span.ToPayload()).ToArray(),
        };
        if (Method is not null) payload["method"] = Method;
        if (Path is not null) payload["path"] = Path;
        if (PathRaw is not null) payload["path_raw"] = PathRaw;
        if (StatusCode is not null) payload["status_code"] = StatusCode;
        if (JobClass is not null) payload["job_class"] = JobClass;
        if (Queue is not null) payload["queue"] = Queue;
        return payload;
    }
}
