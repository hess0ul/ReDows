namespace ReDows.Core.Apps;

/// <summary>
/// One installed package as winget's own correlation engine sees it: the winget
/// package id, the source it matched (null = installed but unmatched), and the
/// EXACT keys winget extracted — ARP product codes (= Uninstall subkey names)
/// and MSIX package family names.
/// </summary>
public sealed record WingetInstalledMatch(
    string WingetId,
    string? CatalogName,
    IReadOnlyList<string> ProductCodes,
    IReadOnlyList<string> PackageFamilyNames);

/// <summary>
/// Row-level attachment of winget reinstall ids to inventory entries — pure and
/// exact-keys only (a wrong reinstall id is worse than none): ARP entries match
/// by Uninstall subkey name against winget's ProductCodes, MSIX entries by
/// PackageFamilyName. Never destructive: an existing exact hint is only
/// upgraded msstore → winget (same package, more scriptable id).
/// </summary>
public static class WingetCorrelation
{
    public static (IReadOnlyList<AppEntry> Entries, int Attached) Attach(
        IReadOnlyList<AppEntry> entries, IReadOnlyList<WingetInstalledMatch> matches)
    {
        var byProductCode = new Dictionary<string, WingetInstalledMatch>(StringComparer.OrdinalIgnoreCase);
        var byFamilyName = new Dictionary<string, WingetInstalledMatch>(StringComparer.OrdinalIgnoreCase);
        foreach (var match in matches)
        {
            if (match.CatalogName is null)
            {
                continue; // installed but unmatched: stays reinstall:manual
            }

            foreach (var productCode in match.ProductCodes)
            {
                byProductCode.TryAdd(productCode, match);
            }

            foreach (var familyName in match.PackageFamilyNames)
            {
                byFamilyName.TryAdd(familyName, match);
            }
        }

        var attached = 0;
        var updated = new List<AppEntry>(entries.Count);
        foreach (var entry in entries)
        {
            var match = FindMatch(entry, byProductCode, byFamilyName);
            if (match is null || !ShouldAttach(entry, match))
            {
                updated.Add(entry);
                continue;
            }

            attached++;
            updated.Add(entry with
            {
                Reinstall = new ReinstallHint(match.CatalogName!, match.WingetId, ReinstallConfidence.Exact),
            });
        }

        return (updated, attached);
    }

    private static WingetInstalledMatch? FindMatch(
        AppEntry entry,
        Dictionary<string, WingetInstalledMatch> byProductCode,
        Dictionary<string, WingetInstalledMatch> byFamilyName)
    {
        if (entry.Source.StartsWith("arp:", StringComparison.Ordinal)
            && entry.Key.Length > entry.Source.Length + 1)
        {
            var subKeyName = entry.Key[(entry.Source.Length + 1)..];
            return byProductCode.GetValueOrDefault(subKeyName);
        }

        if (entry.Source == "msix" && entry.Key.Length > "msix:".Length)
        {
            return byFamilyName.GetValueOrDefault(entry.Key["msix:".Length..]);
        }

        return null;
    }

    private static bool ShouldAttach(AppEntry entry, WingetInstalledMatch match) => entry.Reinstall switch
    {
        null => true,
        { Kind: "msstore" } => match.CatalogName == "winget", // upgrade to the scriptable id
        _ => false,
    };
}
