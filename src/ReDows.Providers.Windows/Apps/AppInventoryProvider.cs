using System.Security.Principal;
using ReDows.Core.Apps;

namespace ReDows.Providers.Windows.Apps;

/// <summary>
/// Assembles the installed-applications inventory from the real machine
/// (increment 1: ARP + MSIX + optional winget enrichment). Other users'
/// per-user sources are unreadable without elevation: one counted degradation
/// per (profile × source), consistent with the asymmetric policy — never guessed.
/// </summary>
public static class AppInventoryProvider
{
    public static AppInventoryReport Build(bool enrichWithWinget)
    {
        var entries = new List<AppEntry>();
        var sources = new List<SourceAccounting>();
        var degradations = new List<InventoryDegradation>();
        var notes = new List<string>();

        foreach (var result in ArpAppSource.Enumerate())
        {
            entries.AddRange(result.Entries);
            sources.Add(result.Accounting);
        }

        var msix = MsixAppSource.Enumerate();
        entries.AddRange(msix.Entries);
        sources.Add(msix.Accounting);
        if (msix.Degradation is not null)
        {
            degradations.Add(msix.Degradation);
        }

        var chocolatey = ChocolateyAppSource.Enumerate();
        entries.AddRange(chocolatey.Entries);
        sources.Add(chocolatey.Accounting);

        var scoop = ScoopAppSource.Enumerate();
        entries.AddRange(scoop.Entries);
        sources.Add(scoop.Accounting);

        var steam = SteamAppSource.Enumerate();
        entries.AddRange(steam.Entries);
        sources.Add(steam.Accounting);
        degradations.AddRange(steam.Degradations);

        var epic = EpicAppSource.Enumerate();
        entries.AddRange(epic.Entries);
        sources.Add(epic.Accounting);

        var ubisoft = UbisoftAppSource.Enumerate();
        entries.AddRange(ubisoft.Entries);
        sources.Add(ubisoft.Accounting);

        var gog = GogAppSource.Enumerate();
        entries.AddRange(gog.Entries);
        sources.Add(gog.Accounting);

        AddOtherProfileDegradations(degradations);

        IReadOnlyList<AppEntry> finalEntries = entries;
        if (enrichWithWinget)
        {
            // COM first: winget's own correlation engine gives row-level exact
            // keys. The export path is the corroboration-only fallback.
            var com = WingetCatalogEnrichment.Run();
            if (com.Note is not null)
            {
                notes.Add(com.Note);
            }

            if (com.Degradation is null)
            {
                var (updated, attached) = WingetCorrelation.Attach(entries, com.Matches);
                finalEntries = updated;
                notes.Add($"{attached} reinstall id(s) attached row-level (exact keys: ARP product codes, MSIX family names).");
            }
            else
            {
                degradations.Add(com.Degradation);
                var export = WingetEnrichment.Run();
                if (export.Degradation is not null)
                {
                    degradations.Add(export.Degradation);
                }

                if (export.Note is not null)
                {
                    notes.Add(export.Note);
                }

                if (export.ExportedIds.Count > 0)
                {
                    notes.Add("winget reinstallable ids (corroboration list only): "
                        + string.Join(", ", export.ExportedIds.Select(p => p.PackageIdentifier).OrderBy(p => p, StringComparer.OrdinalIgnoreCase)));
                }
            }
        }
        else
        {
            notes.Add("winget enrichment not requested (--enrich-winget): Win32 apps default to manual reinstall.");
        }

        return new AppInventoryReport(
            Entries: finalEntries,
            Sources: sources,
            Degradations: degradations,
            Notes: notes,
            DeclaredLimits: AppInventoryReport.V1Limits);
    }

    private static void AddOtherProfileDegradations(List<InventoryDegradation> degradations)
    {
        try
        {
            var machineEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["SystemDrive"] = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))?.TrimEnd('\\') ?? "C:",
            };
            var currentSid = WindowsIdentity.GetCurrent().User?.Value;

            foreach (var profile in ProfileProvider.Discover(machineEnvironment))
            {
                if (string.Equals(profile.Sid, currentSid, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                degradations.Add(new InventoryDegradation("arp:hkcu", $"profile:{profile.UserName}",
                    "another user's hive is unreadable without elevation — per-user installs not inventoried"));
                degradations.Add(new InventoryDegradation("msix", $"profile:{profile.UserName}",
                    "another user's packages require elevation — Store apps not inventoried"));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
        {
            degradations.Add(new InventoryDegradation("profiles", "ProfileList",
                $"profile discovery failed ({ex.GetType().Name}) — other-user coverage unknown"));
        }
    }
}
