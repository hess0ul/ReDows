using System.Text.Json;
using System.Xml.Linq;

namespace ReDows.Core.Apps;

/// <summary>Steam library + app manifest reading (pure: text in, records out).</summary>
public static class SteamManifests
{
    public sealed record SteamApp(string AppId, string? Name, string? InstallDir, int? StateFlags);

    /// <summary>Library root paths from steamapps/libraryfolders.vdf (current and legacy shapes).</summary>
    public static IReadOnlyList<string> GetLibraryPaths(string libraryFoldersVdf)
    {
        var root = ValveKeyValues.Parse(libraryFoldersVdf);
        var paths = new List<string>();

        foreach (var (key, child) in root.Children)
        {
            if (int.TryParse(key, out _) && child.GetValue("path") is { Length: > 0 } path)
            {
                paths.Add(path);
            }
        }

        // Legacy shape: "1"  "D:\\SteamLibrary" as direct values.
        foreach (var (key, value) in root.Values)
        {
            if (int.TryParse(key, out _) && value.Length > 0)
            {
                paths.Add(value);
            }
        }

        return paths;
    }

    public static SteamApp? ParseAppManifest(string appManifestAcf)
    {
        var root = ValveKeyValues.Parse(appManifestAcf);
        var appId = root.GetValue("appid");
        if (string.IsNullOrWhiteSpace(appId))
        {
            return null;
        }

        return new SteamApp(
            appId,
            root.GetValue("name"),
            root.GetValue("installdir"),
            int.TryParse(root.GetValue("StateFlags"), out var flags) ? flags : null);
    }
}

/// <summary>Epic Games Launcher install manifests (Data/Manifests/*.item, JSON).</summary>
public static class EpicManifests
{
    public sealed record EpicApp(string AppName, string? DisplayName, string? InstallLocation, string? Version);

    public static EpicApp? ParseItem(string json)
    {
        using var document = JsonDocument.Parse(json);
        var rootElement = document.RootElement;
        if (rootElement.ValueKind != JsonValueKind.Object
            || !rootElement.TryGetProperty("AppName", out var appName)
            || appName.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return new EpicApp(
            appName.GetString()!,
            GetString(rootElement, "DisplayName"),
            GetString(rootElement, "InstallLocation"),
            GetString(rootElement, "AppVersionString"));

        static string? GetString(JsonElement element, string property) =>
            element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }
}

/// <summary>Chocolatey package metadata (lib/&lt;id&gt;/&lt;id&gt;.nuspec, XML).</summary>
public static class ChocolateyNuspec
{
    public sealed record ChocoPackage(string Id, string? Version, string? Title);

    public static ChocoPackage? Parse(string nuspecXml)
    {
        var document = XDocument.Parse(nuspecXml);
        var metadata = document.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "metadata");
        var id = Element(metadata, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return new ChocoPackage(id, Element(metadata, "version"), Element(metadata, "title"));

        static string? Element(XElement? parent, string localName) =>
            parent?.Elements().FirstOrDefault(e => e.Name.LocalName == localName)?.Value;
    }
}

/// <summary>Scoop per-app install records (apps/&lt;name&gt;/current/install.json + manifest.json).</summary>
public static class ScoopManifests
{
    public static string? ParseBucket(string installJson)
    {
        using var document = JsonDocument.Parse(installJson);
        return document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("bucket", out var bucket)
            && bucket.ValueKind == JsonValueKind.String
                ? bucket.GetString()
                : null;
    }

    public static string? ParseVersion(string manifestJson)
    {
        using var document = JsonDocument.Parse(manifestJson);
        return document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("version", out var version)
            && version.ValueKind == JsonValueKind.String
                ? version.GetString()
                : null;
    }
}
