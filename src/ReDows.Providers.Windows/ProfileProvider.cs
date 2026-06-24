using Microsoft.Win32;

namespace ReDows.Providers.Windows;

public sealed record DiscoveredProfile(string Sid, string UserName, string RootPath, bool IsCurrentUser);

/// <summary>
/// Discovers real user profiles from HKLM ProfileList (never a Users\* glob —
/// deny-list §0-1): catches relocated profiles and provides the SID. Special
/// profiles (Default, defaultuser0, WDAGUtilityAccount…) are recognized so the
/// orphan detector never flags them.
/// </summary>
public static class ProfileProvider
{
    private const string ProfileListKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

    /// <summary>Folder names under the profiles directory that are not real profiles and not orphans.</summary>
    private static readonly string[] SpecialProfileDirectories =
        ["Default", "Default User", "All Users", "Public", "WDAGUtilityAccount"];

    public static IReadOnlyList<DiscoveredProfile> Discover(
        IReadOnlyDictionary<string, string> machineEnvironment, ICollection<string>? notes = null)
    {
        using var profileList = Registry.LocalMachine.OpenSubKey(ProfileListKey)
            ?? throw new InvalidOperationException("ProfileList registry key not found");

        var currentSid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
        var profiles = new List<DiscoveredProfile>();

        foreach (var sid in profileList.GetSubKeyNames())
        {
            // S-1-5-21-* = real accounts; S-1-5-18/19/20 = system/service profiles.
            if (!sid.StartsWith("S-1-5-21-", StringComparison.Ordinal))
            {
                continue;
            }

            using var key = profileList.OpenSubKey(sid);
            var rawPath = key?.GetValue("ProfileImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                // A real account whose root is unknown escapes both profile
                // coverage AND the orphan sweep (which only scans the profiles
                // directory): counted, never silent (§0-5).
                notes?.Add($"profile {sid} has no ProfileImagePath — its tree (if any) is not covered by per-profile rules");
                continue;
            }

            var rootPath = ExpandMachineTokens(rawPath, machineEnvironment);
            profiles.Add(new DiscoveredProfile(
                sid,
                Path.GetFileName(rootPath.TrimEnd('\\', '/')),
                rootPath,
                string.Equals(sid, currentSid, StringComparison.OrdinalIgnoreCase)));
        }

        return profiles;
    }

    /// <summary>
    /// Directories under the profiles root that belong to no ProfileList entry and
    /// are not special: deleted accounts with kept files, migrations — user data
    /// off the profile radar (§A.7/§C-21 → high-priority REVIEW downstream).
    /// </summary>
    public static IReadOnlyList<string> FindOrphanDirectories(
        IEnumerable<string> profilesDirectoryEntries, IEnumerable<string> profileRootPaths)
    {
        // Full paths, not leaf names: a profile relocated to D:\Profiles\alice
        // must not shadow a stale C:\Users\alice full of off-radar user files.
        var knownRoots = new HashSet<string>(
            profileRootPaths.Select(NormalizeForComparison),
            StringComparer.OrdinalIgnoreCase);

        return profilesDirectoryEntries
            .Select(e => e.TrimEnd('\\', '/'))
            .Where(e =>
            {
                var name = Path.GetFileName(e);
                return !knownRoots.Contains(NormalizeForComparison(e))
                    && !SpecialProfileDirectories.Contains(name, StringComparer.OrdinalIgnoreCase)
                    && !name.StartsWith("defaultuser", StringComparison.OrdinalIgnoreCase);
            })
            .ToList();

        static string NormalizeForComparison(string path) =>
            path.TrimEnd('\\', '/').Replace('/', '\\');
    }

    public static string GetProfilesDirectory(IReadOnlyDictionary<string, string> machineEnvironment)
    {
        using var profileList = Registry.LocalMachine.OpenSubKey(ProfileListKey);
        var raw = profileList?.GetValue("ProfilesDirectory", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        return ExpandMachineTokens(raw ?? @"%SystemDrive%\Users", machineEnvironment);
    }

    internal static string ExpandMachineTokens(string raw, IReadOnlyDictionary<string, string> machineEnvironment)
    {
        var result = raw;
        foreach (var (name, value) in machineEnvironment)
        {
            result = result.Replace($"%{name}%", value, StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }
}
