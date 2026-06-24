using ReDows.Core.Scanning;

namespace ReDows.Providers.Windows;

/// <summary>The real machine's ScanContext plus display/report-only discovery details.</summary>
public sealed record WindowsContext(
    ScanContext Context,
    IReadOnlyList<DiscoveredVolume> AllVolumes,
    IReadOnlyList<string> Notes);

/// <summary>
/// Builds the engine's ScanContext from the real machine, read-only:
/// volumes by GUID, machine environment from authoritative sources (never the
/// inherited process environment), profiles from ProfileList with per-profile
/// hive resolution, orphan profile directories.
/// </summary>
public static class WindowsScanContextProvider
{
    public static WindowsContext Build()
    {
        var notes = new List<string>();
        var machineEnvironment = BuildMachineEnvironment(notes);

        var allVolumes = VolumeProvider.Discover();
        var scannedVolumes = allVolumes
            .Where(v => v.Scannable)
            .Select(v => new VolumeInfo(
                RootPath: v.PreferredMountPath!,
                VolumeGuid: v.GuidPath,
                MountPaths: v.MountPaths,
                Label: v.Label,
                FileSystemFormat: v.FileSystemFormat,
                TotalBytes: v.TotalBytes))
            .ToList();

        foreach (var excluded in allVolumes.Where(v => !v.Scannable))
        {
            notes.Add($"volume {excluded.GuidPath} excluded: {excluded.ExclusionReason}");
        }

        var discoveredProfiles = ProfileProvider.Discover(machineEnvironment, notes);
        var profiles = new List<UserProfileInfo>();
        foreach (var discovered in discoveredProfiles)
        {
            var state = ProfileStateResolver.Resolve(discovered, machineEnvironment);
            notes.AddRange(state.Notes);
            profiles.Add(new UserProfileInfo(
                discovered.Sid,
                discovered.UserName,
                discovered.RootPath,
                state.Environment,
                state.KnownFolders,
                state.HiveResolved));
        }

        var orphans = FindOrphans(machineEnvironment, discoveredProfiles, notes);

        var context = new ScanContext(machineEnvironment, scannedVolumes, profiles, orphans);
        return new WindowsContext(context, allVolumes, notes);
    }

    private static IReadOnlyList<string> FindOrphans(
        IReadOnlyDictionary<string, string> machineEnvironment,
        IReadOnlyList<DiscoveredProfile> profiles,
        List<string> notes)
    {
        var profilesDirectory = ProfileProvider.GetProfilesDirectory(machineEnvironment);
        try
        {
            return ProfileProvider.FindOrphanDirectories(
                Directory.EnumerateDirectories(profilesDirectory),
                profiles.Select(p => p.RootPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            notes.Add($"profiles directory '{profilesDirectory}' not enumerable ({ex.GetType().Name}) — orphan detection unavailable");
            return [];
        }
    }

    private static Dictionary<string, string> BuildMachineEnvironment(List<string> notes)
    {
        // Authoritative sources, not the inherited process environment (§0-4):
        // the process env mixes machine values with user overrides.
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        // ProgramW6432 too: the registry, not the inherited process environment
        // (a 32-bit or env-stripped host would silently mis-anchor those rules).
        string? programW6432;
        using (var currentVersion = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                   @"SOFTWARE\Microsoft\Windows\CurrentVersion"))
        {
            programW6432 = currentVersion?.GetValue("ProgramW6432Dir") as string;
        }

        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = windows,
            ["SystemDrive"] = Path.GetPathRoot(windows)?.TrimEnd('\\') ?? "C:",
            ["ProgramData"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            ["ProgramFiles"] = programFiles,
            ["ProgramFiles(x86)"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            ["ProgramW6432"] = programW6432 ?? programFiles,
        };

        environment["AllUsersProfile"] = environment["ProgramData"];

        var publicRoot = KnownFolderCatalog.ResolveForCurrentSession(KnownFolderCatalog.Public);
        if (publicRoot is not null)
        {
            environment["Public"] = publicRoot;
        }
        else
        {
            notes.Add("FOLDERID_Public not resolved — %Public%-anchored rules will not be instantiated");
        }

        return environment;
    }
}
