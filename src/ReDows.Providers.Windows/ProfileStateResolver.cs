using Microsoft.Win32;

namespace ReDows.Providers.Windows;

public sealed record ProfileState(
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyDictionary<string, string> KnownFolders,
    bool HiveResolved,
    IReadOnlyList<string> Notes);

/// <summary>
/// Resolves a profile's environment and Known Folders from ITS OWN hive
/// (User Shell Folders read raw — DoNotExpandEnvironmentNames — then re-anchored
/// on the target profile root: the classic pitfall is letting the registry API
/// substitute the CALLING user's %USERPROFILE%). When the hive is not readable
/// (other user, no elevation), only certain values are returned and the profile
/// is flagged: downstream, rules with unresolved tokens are NOT instantiated
/// (asymmetric policy) and the profile's tree falls to counted REVIEW.
/// </summary>
public static class ProfileStateResolver
{
    private const string UserShellFoldersKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders";

    public static ProfileState Resolve(DiscoveredProfile profile, IReadOnlyDictionary<string, string> machineEnvironment)
    {
        if (profile.IsCurrentUser)
        {
            return ResolveCurrentUser(profile, machineEnvironment);
        }

        try
        {
            using var hive = Registry.Users.OpenSubKey(profile.Sid);
            if (hive is null)
            {
                return Unresolved(profile, "hive not mounted (user not logged in) — offline NTUSER.DAT parsing arrives with the elevated mode block");
            }

            return ResolveFromHive(profile, hive, machineEnvironment);
        }
        catch (System.Security.SecurityException)
        {
            return Unresolved(profile, "hive access denied (no elevation)");
        }
    }

    private static ProfileState ResolveCurrentUser(
        DiscoveredProfile profile, IReadOnlyDictionary<string, string> machineEnvironment)
    {
        var notes = new List<string>();
        var knownFolders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Current session: SHGetKnownFolderPath is authoritative (follows
        // redirections incl. OneDrive Known Folder Move), resolved by GUID.
        foreach (var spec in KnownFolderCatalog.PerProfile)
        {
            var path = KnownFolderCatalog.ResolveForCurrentSession(spec.FolderId);
            if (path is not null)
            {
                knownFolders[spec.CanonicalName] = path;
            }
            else
            {
                notes.Add($"Known Folder '{spec.CanonicalName}' not resolved by the shell");
            }
        }

        var environment = BuildEnvironment(profile, ReadHiveTemp(Registry.CurrentUser, profile, machineEnvironment), knownFolders);
        return new ProfileState(environment, knownFolders, HiveResolved: true, notes);
    }

    private static ProfileState ResolveFromHive(
        DiscoveredProfile profile, RegistryKey hive, IReadOnlyDictionary<string, string> machineEnvironment)
    {
        var notes = new List<string>();
        var knownFolders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using var shellFolders = hive.OpenSubKey(UserShellFoldersKey);
        foreach (var spec in KnownFolderCatalog.PerProfile)
        {
            string? raw = null;
            if (shellFolders is not null)
            {
                foreach (var valueName in spec.UserShellFolderValueNames)
                {
                    raw = shellFolders.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                    if (raw is not null)
                    {
                        break;
                    }
                }
            }

            // Absent value = folder at its documented default, relative to the profile root.
            var path = raw is null
                ? Path.Combine(profile.RootPath, spec.DefaultRelativePath)
                : ExpandForProfile(raw, profile, machineEnvironment);

            if (path.Contains('%'))
            {
                notes.Add($"Known Folder '{spec.CanonicalName}' has unresolvable tokens ('{raw}') — left unresolved");
            }
            else
            {
                knownFolders[spec.CanonicalName] = path;
            }
        }

        var environment = BuildEnvironment(profile, ReadHiveTemp(hive, profile, machineEnvironment), knownFolders);
        return new ProfileState(environment, knownFolders, HiveResolved: true, notes);
    }

    private static ProfileState Unresolved(DiscoveredProfile profile, string reason)
    {
        // Only the root is certain (from ProfileList). No guessed defaults: an
        // ignore rule instantiated on a guessed path could silently lose data.
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["UserProfile"] = profile.RootPath,
        };

        return new ProfileState(
            environment,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            HiveResolved: false,
            [$"profile '{profile.UserName}': {reason}; rules needing its hive are not instantiated, its tree falls to counted REVIEW"]);
    }

    private static string? ReadHiveTemp(
        RegistryKey hive, DiscoveredProfile profile, IReadOnlyDictionary<string, string> machineEnvironment)
    {
        using var environment = hive.OpenSubKey("Environment");
        var raw = environment?.GetValue("TEMP", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        return raw is null ? null : ExpandForProfile(raw, profile, machineEnvironment);
    }

    private static Dictionary<string, string> BuildEnvironment(
        DiscoveredProfile profile, string? hiveTemp, IReadOnlyDictionary<string, string> knownFolders)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["UserProfile"] = profile.RootPath,
            // AppData/LocalAppData are Known Folders too — resolved, not guessed.
            ["AppData"] = knownFolders.TryGetValue("AppData", out var roaming)
                ? roaming
                : Path.Combine(profile.RootPath, @"AppData\Roaming"),
            ["LocalAppData"] = knownFolders.TryGetValue("LocalAppData", out var local)
                ? local
                : Path.Combine(profile.RootPath, @"AppData\Local"),
        };

        environment["Temp"] = hiveTemp ?? Path.Combine(environment["LocalAppData"], "Temp");
        return environment;
    }

    private static string ExpandForProfile(
        string raw, DiscoveredProfile profile, IReadOnlyDictionary<string, string> machineEnvironment)
    {
        var result = raw.Replace("%USERPROFILE%", profile.RootPath, StringComparison.OrdinalIgnoreCase);
        return ProfileProvider.ExpandMachineTokens(result, machineEnvironment);
    }
}
