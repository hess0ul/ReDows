using System.Text.Json;

namespace ReDows.Core.Settings;

/// <summary>
/// Pure parsing of the JSON produced by the feature/capability/appx PowerShell
/// queries (read-only). `ConvertTo-Json` does not wrap a single object in an
/// array, so every parser accepts both a JSON array and a lone object. No process
/// launch — testable on fixtures.
/// </summary>
public static class FeatureQuery
{
    /// <summary>Map of <paramref name="nameField"/> → State (e.g. FeatureName → "Enabled").</summary>
    public static IReadOnlyDictionary<string, string> ParseStates(string? json, string nameField)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ForEachObject(json, element =>
        {
            if (TryGetString(element, nameField, out var name) && TryGetString(element, "State", out var state))
            {
                map[name] = state;
            }
        });
        return map;
    }

    /// <summary>Set of <paramref name="nameField"/> values (e.g. installed package Names).</summary>
    public static IReadOnlySet<string> ParseNames(string? json, string nameField)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ForEachObject(json, element =>
        {
            if (TryGetString(element, nameField, out var name))
            {
                set.Add(name);
            }
        });
        return set;
    }

    private static void ForEachObject(string? json, Action<JsonElement> onObject)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in root.EnumerateArray())
                {
                    if (element.ValueKind == JsonValueKind.Object)
                    {
                        onObject(element);
                    }
                }
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                onObject(root);
            }
        }
    }

    private static bool TryGetString(JsonElement element, string property, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(property, out var child))
        {
            return false;
        }

        value = child.ValueKind == JsonValueKind.String ? child.GetString() ?? string.Empty : child.ToString();
        return value.Length > 0;
    }
}
