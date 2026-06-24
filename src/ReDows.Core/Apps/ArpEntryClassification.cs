using System.Text.RegularExpressions;

namespace ReDows.Core.Apps;

/// <summary>
/// Pure classification of one Add/Remove-Programs (Uninstall) registry entry,
/// modeled on winget's own ARP source and appwiz.cpl: never a deletion, always
/// a visible bucket. Kept in Core so the rules are testable without a registry.
/// </summary>
public static partial class ArpEntryClassification
{
    [GeneratedRegex(@"^(KB\d{6,})\b|^(Update|Security Update|Hotfix|Service Pack) (for|Rollup)\b", RegexOptions.IgnoreCase)]
    private static partial Regex UpdateNamePattern();

    private static readonly string[] UpdateReleaseTypes =
        ["Security Update", "Update Rollup", "Hotfix", "Service Pack", "Update"];

    public static (AppEntryKind Kind, string? Note) Classify(
        string? displayName, int? systemComponent, string? parentKeyName, string? releaseType)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return (AppEntryKind.Component, "no display name (hidden from Add/Remove Programs)");
        }

        if (systemComponent == 1)
        {
            return (AppEntryKind.Component, "SystemComponent=1 (hidden from Add/Remove Programs; some real tools do this — review)");
        }

        if (!string.IsNullOrWhiteSpace(parentKeyName))
        {
            return (AppEntryKind.Update, $"update of '{parentKeyName}'");
        }

        if (releaseType is not null && UpdateReleaseTypes.Contains(releaseType, StringComparer.OrdinalIgnoreCase))
        {
            return (AppEntryKind.Update, $"release type '{releaseType}'");
        }

        if (UpdateNamePattern().IsMatch(displayName))
        {
            return (AppEntryKind.Update, "update-style display name");
        }

        return (AppEntryKind.App, null);
    }
}
