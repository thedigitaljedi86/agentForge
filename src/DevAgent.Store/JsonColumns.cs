namespace DevAgent.Store;

using System.Text.Json;

/// <summary>Helpers for the JSON-array/object columns used by the store entities.</summary>
public static class JsonColumns
{
    public static IReadOnlyList<string> ToList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<string>();
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(json!) ?? Array.Empty<string>();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    public static string FromList(IEnumerable<string>? values) =>
        JsonSerializer.Serialize(values?.ToArray() ?? Array.Empty<string>());
}
