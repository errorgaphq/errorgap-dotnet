using System.Collections.Generic;

namespace Errorgap;

public sealed class ApmSpan
{
    public string Kind { get; set; } = "db";
    public string? Sql { get; set; }
    public string? File { get; set; }
    public int? Line { get; set; }
    public string? Function { get; set; }
    public double DurationMs { get; set; }

    public static ApmSpan Database(
        string sql,
        double durationMs,
        string? file = null,
        int? line = null,
        string? function = null)
        => new()
        {
            Kind = "db",
            Sql = sql,
            File = file,
            Line = line,
            Function = function,
            DurationMs = durationMs,
        };

    internal IDictionary<string, object?> ToPayload()
    {
        var payload = new Dictionary<string, object?>
        {
            ["kind"] = Kind,
            ["duration_ms"] = DurationMs,
        };
        if (Sql is not null) payload["sql"] = Sql;
        if (File is not null) payload["file"] = File;
        if (Line is not null) payload["line"] = Line;
        if (Function is not null) payload["fn_name"] = Function;
        return payload;
    }
}
