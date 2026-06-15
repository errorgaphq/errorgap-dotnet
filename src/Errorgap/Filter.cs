using System.Collections.Generic;
using System.Linq;

namespace Errorgap;

public static class Filter
{
    public const string FilteredValue = "[FILTERED]";

    public static IDictionary<string, object?> Params(
        IDictionary<string, object?>? value,
        IReadOnlyList<string> filterKeys)
    {
        if (value is null || value.Count == 0)
        {
            return new Dictionary<string, object?>();
        }
        var lowered = filterKeys.Select(k => k.ToLowerInvariant()).ToArray();
        return Walk(value, lowered);
    }

    private static IDictionary<string, object?> Walk(
        IDictionary<string, object?> value,
        string[] lowered)
    {
        var output = new Dictionary<string, object?>(value.Count);
        foreach (var (key, val) in value)
        {
            if (IsSensitive(key, lowered))
            {
                output[key] = FilteredValue;
            }
            else if (val is IDictionary<string, object?> nested)
            {
                output[key] = Walk(nested, lowered);
            }
            else
            {
                output[key] = val;
            }
        }
        return output;
    }

    private static bool IsSensitive(string key, string[] lowered)
    {
        var lk = key.ToLowerInvariant();
        foreach (var needle in lowered)
        {
            if (lk.Contains(needle)) return true;
        }
        return false;
    }
}
