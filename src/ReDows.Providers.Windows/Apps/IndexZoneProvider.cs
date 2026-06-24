using ReDows.Core.Apps;
using ReDows.Core.Rules;
using ReDows.Core.Scanning;

namespace ReDows.Providers.Windows.Apps;

/// <summary>
/// An INDEX_EXTERNE finding that needs human attention instead of (or besides) a
/// claimed zone: target volume absent (pre-reset ALERT — that data would be
/// forgotten), stale index entry, or an unreadable/unparsable index file.
/// </summary>
public sealed record IndexZoneNote(string Id, string TargetPath, string Message, bool VolumeAbsent = false);

/// <summary>Everything the index files yielded: zones to claim + notes to surface.</summary>
public sealed record IndexZoneDiscovery(IReadOnlyList<ClaimedZone> Zones, IReadOnlyList<IndexZoneNote> Notes);

/// <summary>
/// Reads app index files (INDEX_EXTERNE, deny-list §D-15) on the real machine and
/// turns the data locations they reference into dynamic claimed zones:
/// relocated Gecko profiles (profiles.ini IsRelative=0), Thunderbird mail stores
/// (prefs.js mail.server.*.directory), Calibre libraries (global.py.json),
/// VirtualBox machine folders (VirtualBox.xml). Parsing is pure Core
/// (<see cref="ReDows.Core.Apps"/>); this type only locates and reads the files.
/// Safety direction: zones may only review/capture (ClaimedZone enforces it), so
/// a wrong index over-captures — never loses. Failures become counted notes.
/// </summary>
public static class IndexZoneProvider
{
    public static IndexZoneDiscovery Discover(ScanContext context)
    {
        var zones = new List<ClaimedZone>();
        var notes = new List<IndexZoneNote>();
        var seenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var profile in context.Profiles)
        {
            // The roaming dir comes from the profile's resolved Known Folders when
            // available. The default-location fallback is safe here because claims
            // are additive-only (review/capture): a wrong guess cannot ignore data.
            var roaming = profile.Environment.TryGetValue("AppData", out var resolved)
                ? resolved
                : Path.Combine(profile.RootPath, @"AppData\Roaming");

            DiscoverGeckoFamily(profile, Path.Combine(roaming, "Mozilla", "Firefox"),
                "firefox", Verdict.CaptureConfig, withMailStores: false, zones, notes, seenTargets);
            DiscoverGeckoFamily(profile, Path.Combine(roaming, "Thunderbird"),
                "thunderbird", Verdict.CaptureUser, withMailStores: true, zones, notes, seenTargets);
            DiscoverCalibre(profile, roaming, zones, notes, seenTargets);
            DiscoverVirtualBox(profile, zones, notes, seenTargets);
        }

        return new IndexZoneDiscovery(zones, notes);
    }

    private static void DiscoverGeckoFamily(
        UserProfileInfo profile, string vendorRoot, string app, Verdict profileVerdict, bool withMailStores,
        List<ClaimedZone> zones, List<IndexZoneNote> notes, HashSet<string> seenTargets)
    {
        var profileZoneId = $"index:{app}:profile@{profile.UserName}";
        var ini = TryReadIndex(Path.Combine(vendorRoot, "profiles.ini"), profileZoneId, notes);
        if (ini is null)
        {
            return;
        }

        foreach (var profilePath in GeckoProfilesIni.GetProfilePaths(ini, vendorRoot))
        {
            // In-place profiles are already covered by the app's rules; only
            // relocated ones (IsRelative=0, arbitrary location) need a claim.
            if (!IsUnder(profilePath, vendorRoot))
            {
                AddZone(profileZoneId, profilePath, profileVerdict,
                    $"Relocated {app} profile (profiles.ini IsRelative=0).", zones, notes, seenTargets);
            }

            if (!withMailStores)
            {
                continue;
            }

            var mailZoneId = $"index:{app}:mailstore@{profile.UserName}";
            var prefs = TryReadIndex(Path.Combine(profilePath, "prefs.js"), mailZoneId, notes);
            if (prefs is null)
            {
                continue;
            }

            foreach (var store in ThunderbirdPrefs.GetMailStoreDirectories(prefs))
            {
                if (!IsUnder(store, profilePath) && !IsUnder(store, vendorRoot))
                {
                    AddZone(mailZoneId, store, Verdict.CaptureUser,
                        "Mail store relocated outside the profile (mail.server.*.directory).", zones, notes, seenTargets);
                }
            }
        }
    }

    private static void DiscoverCalibre(
        UserProfileInfo profile, string roaming,
        List<ClaimedZone> zones, List<IndexZoneNote> notes, HashSet<string> seenTargets)
    {
        var id = $"index:calibre:library@{profile.UserName}";
        var indexPath = Path.Combine(roaming, "calibre", "global.py.json");
        var json = TryReadIndex(indexPath, id, notes);
        if (json is null)
        {
            return;
        }

        IReadOnlyList<string> libraries;
        try
        {
            libraries = CalibreGlobalPrefs.GetLibraryPaths(json);
        }
        catch (System.Text.Json.JsonException ex)
        {
            notes.Add(new IndexZoneNote(id, indexPath,
                $"index file unparsable ({ex.GetType().Name}) — its libraries cannot be claimed"));
            return;
        }

        foreach (var library in libraries)
        {
            AddZone(id, library, Verdict.CaptureUser,
                "Calibre library (global.py.json) — every location ever used, not just the default.", zones, notes, seenTargets);
        }
    }

    private static void DiscoverVirtualBox(
        UserProfileInfo profile,
        List<ClaimedZone> zones, List<IndexZoneNote> notes, HashSet<string> seenTargets)
    {
        var machineZoneId = $"index:virtualbox:machine@{profile.UserName}";
        var indexPath = Path.Combine(profile.RootPath, ".VirtualBox", "VirtualBox.xml");
        var xml = TryReadIndex(indexPath, machineZoneId, notes);
        if (xml is null)
        {
            return;
        }

        IReadOnlyList<string> paths;
        try
        {
            paths = VirtualBoxConfig.GetMachinePaths(xml);
        }
        catch (System.Xml.XmlException ex)
        {
            notes.Add(new IndexZoneNote(machineZoneId, indexPath,
                $"index file unparsable ({ex.GetType().Name}) — its machines cannot be claimed"));
            return;
        }

        foreach (var path in paths)
        {
            if (path.EndsWith(".vbox", StringComparison.OrdinalIgnoreCase))
            {
                // The machine FOLDER holds the disks (.vdi…) and snapshots, not
                // just the .vbox settings file: claim the whole parent.
                var machineFolder = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(machineFolder))
                {
                    notes.Add(new IndexZoneNote(machineZoneId, path,
                        "machine entry has no parent directory — cannot be claimed"));
                    continue;
                }

                AddZone(machineZoneId, machineFolder, Verdict.Review,
                    "VirtualBox machine folder (registered MachineEntry): disks may be huge — human decides.", zones, notes, seenTargets);
            }
            else
            {
                AddZone($"index:virtualbox:default_folder@{profile.UserName}", path, Verdict.Review,
                    "VirtualBox default machine folder.", zones, notes, seenTargets);
            }
        }
    }

    /// <summary>
    /// Validates a target and claims it. Volume absent → pre-reset ALERT note (no
    /// zone: nothing to walk, but the human MUST know that referenced data is not
    /// in the inventory). Target missing on a present volume → stale-index note,
    /// claimed anyway (harmless if truly gone, protective if it appears).
    /// </summary>
    private static void AddZone(
        string id, string target, Verdict verdict, string note,
        List<ClaimedZone> zones, List<IndexZoneNote> notes, HashSet<string> seenTargets)
    {
        if (!Path.IsPathRooted(target))
        {
            notes.Add(new IndexZoneNote(id, target, "index target is not an absolute path — cannot be claimed"));
            return;
        }

        if (!seenTargets.Add(ScanPaths.Normalize(target)))
        {
            return;
        }

        var volumeRoot = ScanPaths.AnchorDriveRoot(Path.GetPathRoot(target)!);
        if (!Directory.Exists(volumeRoot))
        {
            notes.Add(new IndexZoneNote(id, target,
                $"target volume '{volumeRoot}' is ABSENT — plug it back and re-scan, or this data will be missing from the inventory",
                VolumeAbsent: true));
            return;
        }

        if (!Directory.Exists(target))
        {
            notes.Add(new IndexZoneNote(id, target,
                "target missing on a present volume (stale index entry?) — verify manually"));
        }

        zones.Add(new ClaimedZone(id, target, verdict, note));
    }

    /// <summary>
    /// Real index files are a few KB; far beyond this is corruption or an
    /// attack, and an unbounded read could OOM and lose the whole scan.
    /// </summary>
    private const long MaxIndexBytes = 64 * 1024 * 1024;

    /// <summary>
    /// Reads an index file. Absent file/dir = app not installed or no relocated
    /// profile: silent skip. Anything else (locked, oversized, other user's
    /// profile without elevation…) is a counted note — never a silent gap (§0-5).
    /// </summary>
    private static string? TryReadIndex(string path, string id, List<IndexZoneNote> notes)
    {
        try
        {
            if (new FileInfo(path).Length > MaxIndexBytes)
            {
                notes.Add(new IndexZoneNote(id, path,
                    $"index file larger than {MaxIndexBytes / (1024 * 1024)} MB (corrupt?) — its targets cannot be claimed"));
                return null;
            }

            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            notes.Add(new IndexZoneNote(id, path,
                $"index file unreadable ({ex.GetType().Name}) — its targets cannot be claimed"));
            return null;
        }
    }

    private static bool IsUnder(string path, string root)
    {
        var normalizedPath = ScanPaths.Normalize(path);
        var normalizedRoot = ScanPaths.Normalize(root);
        return normalizedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || (normalizedPath.Length > normalizedRoot.Length
                && normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
                && normalizedPath[normalizedRoot.Length] == '/');
    }
}
