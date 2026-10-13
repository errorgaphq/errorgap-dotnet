using System.Collections.Generic;
using System.Linq;

namespace Errorgap;

public static class Filter
{
    public const string FilteredValue = "[FILTERED]";

    public const string TruncatedValue = "[TRUNCATED]";
    private const int MaxString = 4096;
    private const int MaxItems = 128;
    private const int MaxDepth = 8;

    /// <summary>
    /// Mask sensitive keys at any depth (inside lists too) and cut long
    /// strings and large or deeply nested collections.
    /// </summary>
    public static IDictionary<string, object?> Params(
        IDictionary<string, object?>? value,
        IReadOnlyList<string> filterKeys)
    {
        if (value is null || value.Count == 0)
        {
            return new Dictionary<string, object?>();
        }
        var lowered = filterKeys.Select(k => k.ToLowerInvariant()).ToArray();
        return WalkMap(value.Select(pair => (pair.Key, pair.Value)), lowered, 0);
    }

    public static string Truncate(string value, int limit = MaxString) =>
        value.Length > limit ? value.Substring(0, limit) + TruncatedValue : value;

    private static IDictionary<string, object?> WalkMap(
        IEnumerable<(string Key, object? Value)> entries,
        string[] lowered,
        int depth)
    {
        var output = new Dictionary<string, object?>();
        foreach (var (key, val) in entries)
        {
            if (output.Count >= MaxItems) break;
            output[key] = IsSensitive(key, lowered) ? FilteredValue : Walk(val, lowered, depth + 1);
        }
        return output;
    }

    private static object? Walk(object? value, string[] lowered, int depth)
    {
        switch (value)
        {
            case null:
                return null;
            case string text:
                return Truncate(text);
            case IDictionary<string, object?> map:
                return depth >= MaxDepth
                    ? TruncatedValue
                    : WalkMap(map.Select(pair => (pair.Key, pair.Value)), lowered, depth);
            case IDictionary<string, string> strings:
                return depth >= MaxDepth
                    ? TruncatedValue
                    : WalkMap(strings.Select(pair => (pair.Key, (object?)pair.Value)), lowered, depth);
            case System.Collections.IDictionary legacy:
                if (depth >= MaxDepth) return TruncatedValue;
                var entries = new List<(string, object?)>();
                foreach (System.Collections.DictionaryEntry entry in legacy)
                {
                    entries.Add((entry.Key?.ToString() ?? "null", entry.Value));
                }
                return WalkMap(entries, lowered, depth);
            case System.Collections.IEnumerable items when value is not byte[]:
                if (depth >= MaxDepth) return TruncatedValue;
                var list = new List<object?>();
                foreach (var item in items)
                {
                    if (list.Count >= MaxItems) break;
                    list.Add(Walk(item, lowered, depth + 1));
                }
                return list;
            default:
                return value;
        }
    }

    private static bool IsSensitive(string key, string[] lowered)
    {
        var lk = key.ToLowerInvariant();
        foreach (var needle in lowered)
        {
            if (needle.Length > 0 && lk.Contains(needle)) return true;
        }
        return false;
    }
}
