using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ReDows.Core.Apps;

// INDEX_EXTERNE parsers (deny-list §D-15): app configs that POINT at data living
// elsewhere (relocated profiles, mail stores, libraries, VM folders). All pure
// text-in/paths-out, testable on fixtures; the targets they yield become claimed
// zones (review/capture only — a wrong index over-captures, never loses).

/// <summary>Gecko profiles.ini: profile directories, including absolute relocated ones.</summary>
public static partial class GeckoProfilesIni
{
    [GeneratedRegex(@"^\[.+\]\s*$")]
    private static partial Regex SectionPattern();

    /// <summary>
    /// Returns every profile path. Relative entries (IsRelative=1 or absent) are
    /// resolved against <paramref name="vendorRoot"/>; IsRelative=0 paths are
    /// returned as-is (arbitrary absolute locations — the whole point §D-15).
    /// </summary>
    public static IReadOnlyList<string> GetProfilePaths(string profilesIni, string vendorRoot)
    {
        var paths = new List<string>();
        string? currentPath = null;
        var currentIsRelative = true;
        var inProfileSection = false;

        foreach (var rawLine in profilesIni.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (SectionPattern().IsMatch(line))
            {
                FlushProfile();
                inProfileSection = line.StartsWith("[Profile", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inProfileSection)
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (key.Equals("Path", StringComparison.OrdinalIgnoreCase))
            {
                currentPath = value;
            }
            else if (key.Equals("IsRelative", StringComparison.OrdinalIgnoreCase))
            {
                currentIsRelative = value != "0";
            }
        }

        FlushProfile();
        return paths;

        void FlushProfile()
        {
            if (currentPath is { Length: > 0 })
            {
                paths.Add(currentIsRelative
                    ? $"{vendorRoot.TrimEnd('/', '\\')}/{currentPath.Replace('\\', '/')}"
                    : currentPath);
            }

            currentPath = null;
            currentIsRelative = true;
        }
    }
}

/// <summary>Thunderbird prefs.js: mail stores possibly relocated outside the profile.</summary>
public static partial class ThunderbirdPrefs
{
    // user_pref("mail.server.server1.directory", "D:\\Mail\\Pop");
    [GeneratedRegex("""user_pref\("mail\.server\.[^"]+\.directory",\s*"((?:[^"\\]|\\.)*)"\)""")]
    private static partial Regex MailDirectoryPattern();

    public static IReadOnlyList<string> GetMailStoreDirectories(string prefsJs)
    {
        var directories = new List<string>();
        foreach (Match match in MailDirectoryPattern().Matches(prefsJs))
        {
            var value = match.Groups[1].Value.Replace(@"\\", @"\");
            if (value.Length > 0)
            {
                directories.Add(value);
            }
        }

        return directories;
    }
}

/// <summary>Chromium Local State: declared browser profiles (never a 'Profile *' glob).</summary>
public static class ChromiumLocalState
{
    public static IReadOnlyList<string> GetProfileDirectoryNames(string localStateJson)
    {
        using var document = JsonDocument.Parse(localStateJson);
        if (!document.RootElement.TryGetProperty("profile", out var profile)
            || !profile.TryGetProperty("info_cache", out var infoCache)
            || infoCache.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        return [.. infoCache.EnumerateObject().Select(p => p.Name)];
    }
}

/// <summary>Calibre global.py.json: every library location (not just the default one).</summary>
public static class CalibreGlobalPrefs
{
    public static IReadOnlyList<string> GetLibraryPaths(string globalPyJson)
    {
        using var document = JsonDocument.Parse(globalPyJson);
        var root = document.RootElement;
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (root.TryGetProperty("library_path", out var libraryPath)
            && libraryPath.ValueKind == JsonValueKind.String
            && libraryPath.GetString() is { Length: > 0 } single
            && seen.Add(single))
        {
            paths.Add(single);
        }

        if (root.TryGetProperty("library_usage_stats", out var stats)
            && stats.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in stats.EnumerateObject())
            {
                if (entry.Name.Length > 0 && seen.Add(entry.Name))
                {
                    paths.Add(entry.Name);
                }
            }
        }

        return paths;
    }
}

/// <summary>VirtualBox.xml: registered machine files and the default machine folder.</summary>
public static class VirtualBoxConfig
{
    public static IReadOnlyList<string> GetMachinePaths(string virtualBoxXml)
    {
        var document = XDocument.Parse(virtualBoxXml);
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var element in document.Descendants())
        {
            switch (element.Name.LocalName)
            {
                case "MachineEntry" when element.Attribute("src")?.Value is { Length: > 0 } src && seen.Add(src):
                    paths.Add(src);
                    break;
                case "SystemProperties" when element.Attribute("defaultMachineFolder")?.Value is { Length: > 0 } folder
                    && seen.Add(folder):
                    paths.Add(folder);
                    break;
            }
        }

        return paths;
    }
}
