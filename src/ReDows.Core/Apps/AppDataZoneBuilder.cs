using ReDows.Core.Rules;
using ReDows.Core.Scanning;

namespace ReDows.Core.Apps;

/// <summary>
/// Turns the application inventory into engine-fed app-data zones (increment 4 —
/// the mirror of <see cref="ReinstallZoneBuilder"/>): for each inventoried app and
/// each user profile, the app's %AppData%\&lt;name&gt; is a CAPTURE zone (config —
/// keep it) and its %LocalAppData%\&lt;name&gt; is a REVIEW zone (mixed config +
/// cache — the human decides). Pure — names + resolved profile roots in, zones out,
/// no I/O.
///
/// The data folder is matched to the app Name (best-effort): an app whose folder is
/// named differently is simply not linked, and a non-existent zone is a harmless
/// no-op (the caller filters to existing folders). Names carrying a path separator,
/// a drive marker or a "."/".." segment are rejected so a zone can never escape the
/// profile's AppData root. Additive-only (never ignore), evaluated only over a
/// REVIEW verdict, so it never overrides an already-classified capture/secret.
/// </summary>
public static class AppDataZoneBuilder
{
    /// <summary>The resolved %AppData% (Roaming) and %LocalAppData% roots of one profile.</summary>
    public readonly record struct ProfileDataRoots(string RoamingRoot, string LocalRoot);

    private static readonly char[] IllegalNameChars = ['/', '\\', ':'];

    public static IReadOnlyList<AppDataZone> Build(
        AppInventoryReport inventory, IReadOnlyList<ProfileDataRoots> profiles)
    {
        var zones = new List<AppDataZone>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var app in inventory.Entries)
        {
            var name = app.Name?.Trim();
            if (string.IsNullOrEmpty(name)
                || name.IndexOfAny(IllegalNameChars) >= 0
                || name is "." or "..")
            {
                continue;
            }

            foreach (var profile in profiles)
            {
                AddZone(zones, seen, app, name, "appdata", $"{profile.RoamingRoot}/{name}", Verdict.CaptureConfig);
                AddZone(zones, seen, app, name, "localdata", $"{profile.LocalRoot}/{name}", Verdict.Review);
            }
        }

        return zones;
    }

    private static void AddZone(
        List<AppDataZone> zones, HashSet<string> seen, AppEntry app, string name, string idPrefix, string rawPath, Verdict verdict)
    {
        var normalized = ScanPaths.Normalize(rawPath);
        if (ScanPaths.Split(normalized).Length < 2 || !seen.Add(normalized))
        {
            return;
        }

        // Distinct id prefixes (appdata: / localdata:) so the report never merges a
        // captured %AppData% and a reviewed %LocalAppData% under one attribution.
        zones.Add(new AppDataZone($"{idPrefix}:{Sanitize(app.Source)}:{Sanitize(name)}", normalized, verdict));
    }

    private static string Sanitize(string value)
    {
        var cleaned = value.Replace('\r', ' ').Replace('\n', ' ').Replace(':', '-').Trim();
        return cleaned.Length == 0 ? "?" : cleaned;
    }
}
