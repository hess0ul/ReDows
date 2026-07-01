using System.IO;
using ReDows.Core.Scanning;

namespace ReDows.Core.Apps;

/// <summary>
/// Turns the application inventory into engine-fed reinstall zones: each app's
/// InstallLocation becomes a <see cref="ReinstallZone"/> whose re-downloadable
/// content the scan may ignore. Pure — inventory in, zones out, no I/O.
///
/// Safety (forget-nothing): a null/empty/relative location is skipped; a
/// too-shallow path (fewer than three segments — a bare volume or a single-folder
/// drive root like D:\App) is rejected so a bogus or over-broad location can never
/// blanket-ignore a whole volume or a system root such as C:\Users. The residual
/// (a hand-placed app directly at a drive root stays REVIEW) is a safe miss.
/// Duplicate locations (two apps, one directory) are collapsed.
/// </summary>
public static class ReinstallZoneBuilder
{
    /// <summary>Minimum path depth (drive + two folders) for an install dir to become an ignore zone.</summary>
    private const int MinDepth = 3;

    public static IReadOnlyList<ReinstallZone> Build(AppInventoryReport inventory)
    {
        var zones = new List<ReinstallZone>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var app in inventory.Entries)
        {
            var location = app.InstallLocation;
            if (string.IsNullOrWhiteSpace(location) || !Path.IsPathRooted(location))
            {
                continue;
            }

            var normalized = ScanPaths.Normalize(location);
            var segments = ScanPaths.Split(normalized);
            if (segments.Length < MinDepth)
            {
                continue;
            }

            // A directory the inventory reports as an install dir but that is NAMED
            // like user data (save/config/keys…) is exactly the uncertain case:
            // keep it in REVIEW rather than let the zone ignore its whole subtree
            // (the ScanEngine data-name net only inspects segments BELOW the zone
            // root, so the zone's own leaf must be vetted here — default-to-review).
            // Note: install locations are taken verbatim from the source, never
            // trimmed upward, so a zone prefix is always a true install root.
            if (AppDataFolders.IsDataName(segments[^1]))
            {
                continue;
            }

            if (!seen.Add(normalized))
            {
                continue;
            }

            zones.Add(new ReinstallZone(ZoneId(app), normalized));
        }

        return zones;
    }

    private static string ZoneId(AppEntry app)
    {
        // 'app:<source>:<name>' — the ':' namespace keeps report attribution from
        // colliding with rule/engine ids (ReinstallZone enforces the ':').
        var name = string.IsNullOrWhiteSpace(app.Name) ? app.Key : app.Name!;
        return $"app:{Sanitize(app.Source)}:{Sanitize(name)}";
    }

    private static string Sanitize(string value)
    {
        // Keep ids single-line; the ':' comes from the id template, not the parts.
        var cleaned = value.Replace('\r', ' ').Replace('\n', ' ').Replace(':', '-').Trim();
        return cleaned.Length == 0 ? "?" : cleaned;
    }
}
