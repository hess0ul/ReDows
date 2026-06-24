using System.Runtime.InteropServices;
using ReDows.Providers.Windows.Native;

namespace ReDows.Providers.Windows;

/// <summary>
/// The Known Folders ReDows resolves per profile, keyed by the canonical names of
/// the ruleset's FOLDERID_* tokens. Resolution is always by GUID — never by
/// (localized) name — per the locale-independence invariant. Each entry knows its
/// 'User Shell Folders' registry value names (GUID string for post-Vista folders,
/// legacy name otherwise) and its default path relative to the profile root.
/// </summary>
public sealed record KnownFolderSpec(
    string CanonicalName,
    Guid FolderId,
    IReadOnlyList<string> UserShellFolderValueNames,
    string DefaultRelativePath);

public static class KnownFolderCatalog
{
    public static IReadOnlyList<KnownFolderSpec> PerProfile { get; } =
    [
        new("Desktop", new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641"), ["{B4BFCC3A-DB2C-424C-B029-7FE99A87C641}", "Desktop"], "Desktop"),
        new("Documents", new("FDD39AD0-238F-46AF-ADB4-6C85480369C7"), ["{FDD39AD0-238F-46AF-ADB4-6C85480369C7}", "Personal"], "Documents"),
        new("Downloads", new("374DE290-123F-4565-9164-39C4925E467B"), ["{374DE290-123F-4565-9164-39C4925E467B}"], "Downloads"),
        new("Pictures", new("33E28130-4E1E-4676-835A-98395C3BC3BB"), ["{33E28130-4E1E-4676-835A-98395C3BC3BB}", "My Pictures"], "Pictures"),
        new("Music", new("4BD8D571-6D19-48D3-BE97-422220080E43"), ["{4BD8D571-6D19-48D3-BE97-422220080E43}", "My Music"], "Music"),
        new("Videos", new("18989B1D-99B5-455B-841C-AB7C74E4DDFC"), ["{18989B1D-99B5-455B-841C-AB7C74E4DDFC}", "My Video"], "Videos"),
        new("SavedGames", new("4C5C32FF-BB9D-43B0-B5B4-2D72E54EAAA4"), ["{4C5C32FF-BB9D-43B0-B5B4-2D72E54EAAA4}"], "Saved Games"),
        new("Favorites", new("1777F761-68AD-4D8A-87BD-30B759FA33DD"), ["{1777F761-68AD-4D8A-87BD-30B759FA33DD}", "Favorites"], "Favorites"),
        new("Contacts", new("56784854-C6CB-462B-8169-88E350ACB882"), ["{56784854-C6CB-462B-8169-88E350ACB882}"], "Contacts"),
        new("Links", new("BFB9D5E0-C6A9-404C-B2B2-AE6DB6AF4968"), ["{BFB9D5E0-C6A9-404C-B2B2-AE6DB6AF4968}"], "Links"),
        new("SavedSearches", new("7D1D3A04-DEBB-4115-95CF-2F29DA2920DA"), ["{7D1D3A04-DEBB-4115-95CF-2F29DA2920DA}"], "Searches"),
        new("CameraRoll", new("AB5FB87B-7CE2-4F83-915D-550846C9537B"), ["{AB5FB87B-7CE2-4F83-915D-550846C9537B}"], @"Pictures\Camera Roll"),
        new("Screenshots", new("B7BEDE81-DF94-4682-A7D8-57A52620B86F"), ["{B7BEDE81-DF94-4682-A7D8-57A52620B86F}"], @"Pictures\Screenshots"),
        // Not rule tokens, but resolved (never guessed) to feed each profile's environment.
        new("AppData", new("3EB685DB-65F9-4CF6-A03A-E3EF65729F3D"), ["{3EB685DB-65F9-4CF6-A03A-E3EF65729F3D}", "AppData"], @"AppData\Roaming"),
        new("LocalAppData", new("F1B32785-6FBA-4FCF-9D55-7B8E7F157091"), ["{F1B32785-6FBA-4FCF-9D55-7B8E7F157091}", "Local AppData"], @"AppData\Local"),
    ];

    public static readonly Guid Public = new("DFDF76A2-C82A-4D63-906A-5644AC457385");

    /// <summary>Resolves a Known Folder for the CURRENT session only (SHGetKnownFolderPath).</summary>
    public static string? ResolveForCurrentSession(Guid folderId)
    {
        // Docs: the returned pointer must be freed whether the call succeeds or not.
        var hr = NativeMethods.SHGetKnownFolderPath(folderId, 0, IntPtr.Zero, out var pathPtr);
        try
        {
            return hr != 0 ? null : Marshal.PtrToStringUni(pathPtr);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pathPtr);
        }
    }
}
