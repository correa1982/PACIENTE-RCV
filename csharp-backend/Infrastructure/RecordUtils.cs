using System.Globalization;

namespace PacienteRcv.Infrastructure;

public static class RecordUtils
{
    public static string GetOrEmpty(Dictionary<string, string> data, string key)
    {
        return data.TryGetValue(key, out var value) ? value ?? string.Empty : string.Empty;
    }

    public static bool IsNullOrWhite(Dictionary<string, string> data, string key)
    {
        return string.IsNullOrWhiteSpace(GetOrEmpty(data, key));
    }

    public static Dictionary<string, object?> ToObjectDictionary(Dictionary<string, string> data)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var kv in data)
        {
            result[kv.Key] = kv.Value ?? string.Empty;
        }
        return result;
    }

    public static string NowTimestamp()
    {
        return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    public static string SanitizeTimestamp(string value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        return value.Replace(" ", "_", StringComparison.Ordinal)
                    .Replace(":", "-", StringComparison.Ordinal);
    }
}
