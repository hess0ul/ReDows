using System.Text.Json;

namespace ReDows.Core.Apps;

/// <summary>
/// Pure parser for `winget export` output (packages.schema.2.0.json):
/// { "Sources": [ { "SourceDetails": { "Name": … }, "Packages": [ { "PackageIdentifier": …, "Version"? } ] } ] }.
/// The export contains ONLY packages winget could match to a source — names and
/// unmatched apps are absent — so this is a corroboration list of reinstallable
/// ids, never the inventory and never a row-level attachment by itself
/// (dynamic sources are corroboration only).
/// </summary>
public static class WingetExport
{
    public sealed record ExportedPackage(string SourceName, string PackageIdentifier);

    public static IReadOnlyList<ExportedPackage> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var packages = new List<ExportedPackage>();

        if (!document.RootElement.TryGetProperty("Sources", out var sources) || sources.ValueKind != JsonValueKind.Array)
        {
            return packages;
        }

        foreach (var source in sources.EnumerateArray())
        {
            var sourceName = source.TryGetProperty("SourceDetails", out var details)
                && details.TryGetProperty("Name", out var name) && name.ValueKind == JsonValueKind.String
                    ? name.GetString()!
                    : "?";

            if (!source.TryGetProperty("Packages", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var package in list.EnumerateArray())
            {
                if (package.TryGetProperty("PackageIdentifier", out var id) && id.ValueKind == JsonValueKind.String)
                {
                    packages.Add(new ExportedPackage(sourceName, id.GetString()!));
                }
            }
        }

        return packages;
    }
}
