using ReDows.Core.Rules.Globbing;

namespace ReDows.Core.Scanning;

/// <summary>
/// Folder-name stems that signal user data inside an application directory — the
/// SAME set the app-zones ruleset uses as its REVIEW carve-backs
/// (rules/app-zones.yaml). Kept here so the engine-fed reinstall zones (install
/// dirs from the inventory, <see cref="ReinstallZone"/>) apply the same
/// forget-nothing net: an install dir is re-acquirable, but a subtree whose name
/// says "user data" is never silently ignored — it stays REVIEW for the human.
/// Collision-prone program names (data, assets, bin, content, cache, logs,
/// plugins) are deliberately absent: they almost always mean program payload.
/// </summary>
public static class AppDataFolders
{
    private static readonly string[] Stems =
    [
        "config*", "save*", "profile*", "preset*", "settings",
        "userdata", "mods", "backup*", "keys", "licen*",
    ];

    /// <summary>True when a path-segment name matches one of the user-data stems (ordinal, case-insensitive).</summary>
    public static bool IsDataName(string segment)
    {
        foreach (var stem in Stems)
        {
            if (GlobPattern.MatchesName(stem, segment))
            {
                return true;
            }
        }

        return false;
    }
}
