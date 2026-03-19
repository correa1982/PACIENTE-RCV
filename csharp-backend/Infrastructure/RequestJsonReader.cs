using System.Text.Json;

namespace PacienteRcv.Infrastructure;

public static class RequestJsonReader
{
    public static async Task<Dictionary<string, string>> ReadDictionaryAsync(HttpRequest request)
    {
        if (request.ContentLength is null or 0)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        using var document = await JsonDocument.ParseAsync(request.Body);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var prop in document.RootElement.EnumerateObject())
        {
            result[prop.Name] = JsonElementToString(prop.Value);
        }

        return result;
    }

    private static string JsonElementToString(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number => element.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => string.Empty,
            _ => element.ToString()
        };
    }
}
