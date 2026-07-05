using System.Diagnostics;
using ReDows.Core.Apps;
using ReDows.Providers.Windows.Apps;

namespace ReDows.Gui.Scanning;

/// <summary>
/// Source of the installed-apps recogniser Review uses to spot app-made folders (ShareX, Adobe…). A
/// seam: the real implementation reads the app inventory (registry only, fast) once; a test swaps a
/// fake. Absence/failure = an empty recogniser, so nothing is app-matched and Review falls back to the
/// memory, rules and AI.
/// </summary>
public interface IInstalledAppsSource
{
    InstalledAppFolders Load();
}

/// <summary>
/// Builds the installed-apps recogniser from the same registry inventory the scan uses (no winget
/// enrichment — this only needs names and publishers, and must stay fast). A failure degrades to an
/// empty recogniser rather than crashing Review.
/// </summary>
public sealed class WindowsInstalledAppsSource : IInstalledAppsSource
{
    public InstalledAppFolders Load()
    {
        try
        {
            var report = AppInventoryProvider.Build(enrichWithWinget: false);
            return new InstalledAppFolders(report.Entries
                .Where(e => e.Kind == AppEntryKind.App)
                .Select(e => (e.Name, e.Publisher)));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Installed-apps recogniser disabled: {ex.Message}");
            return new InstalledAppFolders([]);
        }
    }
}
