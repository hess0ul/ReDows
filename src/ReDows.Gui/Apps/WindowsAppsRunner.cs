using System.IO;
using ReDows.Core.Apps;
using ReDows.Providers.Windows.Apps;

namespace ReDows.Gui.Apps;

/// <summary>
/// The real Apps source: builds this PC's installed-app inventory (optionally winget-enriched, on a
/// background thread) and writes the InDows reinstall profile from the chosen apps. Read-only on the
/// machine when loading; the export writes one small YAML file to a folder the user picks.
/// </summary>
public sealed class WindowsAppsRunner : IAppsRunner
{
    public Task<AppsLoadResult> LoadAsync(bool enrichWithWinget, IProgress<string> progress, CancellationToken cancellationToken) =>
        Task.Run(() => Load(enrichWithWinget, progress, cancellationToken), cancellationToken);

    private static AppsLoadResult Load(bool enrichWithWinget, IProgress<string> progress, CancellationToken cancellationToken)
    {
        progress.Report(enrichWithWinget
            ? "Reading installed apps and matching them to winget (a few seconds)…"
            : "Reading installed apps…");

        var report = AppInventoryProvider.Build(enrichWithWinget);
        cancellationToken.ThrowIfCancellationRequested();

        // Only real applications — the inventory's Component/Update rows (frameworks, KB patches…) are noise here.
        var apps = report.Entries.Where(e => e.Kind == AppEntryKind.App).ToList();
        var auto = apps.Count(e => e.Reinstall is not null);
        var summary = $"{apps.Count:N0} apps · {auto:N0} reinstall automatically · {apps.Count - auto:N0} manual"
            + (enrichWithWinget ? " (winget-matched)" : "");

        return new AppsLoadResult(apps, summary);
    }

    public AppsExportResult Export(IReadOnlyList<AppEntry> selectedApps, string folder)
    {
        var destination = Path.GetFullPath(folder);
        Directory.CreateDirectory(destination);

        // The apps half of the ReDows → InDows hand-off: InDows drops this file in to reinstall via winget.
        var catalog = InDowsCatalogEmitter.Emit(selectedApps);
        var path = Path.Combine(destination, "configuration.dsc.yaml");
        File.WriteAllText(path, catalog.Yaml);

        return new AppsExportResult(path, catalog.ActiveCount, catalog.CandidateCount, catalog.ManualCount);
    }
}
