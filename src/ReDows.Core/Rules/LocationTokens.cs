using System.Collections.Frozen;

namespace ReDows.Core.Rules;

public enum LocationTokenKind
{
    /// <summary>%X% resolved from the machine environment (e.g. %SystemRoot%).</summary>
    MachineEnvironment,

    /// <summary>%X% resolved from each profile's own environment (e.g. %APPDATA%).</summary>
    ProfileEnvironment,

    /// <summary>&lt;UserProfile&gt; — the resolved root of each profile.</summary>
    UserProfileRoot,

    /// <summary>FOLDERID_X — a Known Folder resolved per profile by GUID, never by localized name.</summary>
    KnownFolder,
}

public sealed record LocationToken(LocationTokenKind Kind, string Name);

/// <summary>
/// The closed set of location tokens accepted in rule patterns (locale-independence
/// invariant: a pattern never hardcodes an absolute or localized path). Unknown
/// tokens are a validation error — fail-closed, a typo must never become a no-match.
/// </summary>
public static class LocationTokens
{
    private static readonly FrozenDictionary<string, string> MachineEnvironmentTokens = new[]
    {
        "SystemRoot", "SystemDrive", "ProgramData", "AllUsersProfile",
        "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "Public",
    }.ToFrozenDictionary(n => n, n => n, StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<string, string> ProfileEnvironmentTokens = new[]
    {
        "AppData", "LocalAppData", "Temp", "UserProfile",
    }.ToFrozenDictionary(n => n, n => n, StringComparer.OrdinalIgnoreCase);

    // No FOLDERID_Public* here: the providers do not resolve them per profile,
    // and a token that validates but never instantiates is a silent dead rule
    // (fail-closed). Public folders are reachable via the %Public% machine
    // token; per-GUID support can come back the day a provider feeds it.
    private static readonly FrozenDictionary<string, string> KnownFolderNames = new[]
    {
        "Desktop", "Documents", "Downloads", "Pictures", "Music", "Videos",
        "SavedGames", "Favorites", "Contacts", "Links", "SavedSearches",
        "CameraRoll", "Screenshots",
    }.ToFrozenDictionary(n => n, n => n, StringComparer.OrdinalIgnoreCase);

    public static bool TryResolveMachineEnvironment(string name, out string canonical) =>
        MachineEnvironmentTokens.TryGetValue(name, out canonical!);

    public static bool TryResolveProfileEnvironment(string name, out string canonical) =>
        ProfileEnvironmentTokens.TryGetValue(name, out canonical!);

    public static bool TryResolveKnownFolder(string name, out string canonical) =>
        KnownFolderNames.TryGetValue(name, out canonical!);
}
