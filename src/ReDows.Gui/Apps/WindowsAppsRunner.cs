using System.IO;
using ReDows.Core.Apps;
using ReDows.Core.Profile;
using ReDows.Core.Settings;
using ReDows.Providers.Windows.Apps;
using ReDows.Providers.Windows.Settings;

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

    public Task<AppsExportResult> ExportProfileAsync(IReadOnlyList<AppEntry> selectedApps, string folder, IProgress<string> progress, CancellationToken cancellationToken) =>
        Task.Run(() => ExportProfile(selectedApps, folder, progress, cancellationToken), cancellationToken);

    /// <summary>
    /// Write the full InDows profile folder — the same four files 'redows profile' produces, reusing the
    /// same pure Core builders: configuration.dsc.yaml (apps), settings-profile.json/.md (settings), README.md.
    /// Reads the live settings (registry + a few PowerShell queries) read-only; never reads a secret value.
    /// </summary>
    private static AppsExportResult ExportProfile(IReadOnlyList<AppEntry> selectedApps, string folder, IProgress<string> progress, CancellationToken cancellationToken)
    {
        var destination = Path.GetFullPath(folder);
        Directory.CreateDirectory(destination);

        // Apps half — the winget DSC catalog InDows reinstalls from.
        progress.Report("Building the apps catalog…");
        var catalog = InDowsCatalogEmitter.Emit(selectedApps);
        cancellationToken.ThrowIfCancellationRequested();

        // Settings half — load the catalog (fail-closed) and read the live machine (read-only, non-secret).
        progress.Report("Reading your Windows settings (a few seconds)…");
        var settingsCatalog = SettingsCatalogLoader.LoadDirectory(ResolveSettingsDirectory());
        var report = WindowsSettingsReader.Read(settingsCatalog);
        cancellationToken.ThrowIfCancellationRequested();
        var profile = SettingsProfileBuilder.Build(report);

        progress.Report("Writing the InDows profile…");
        File.WriteAllText(Path.Combine(destination, "configuration.dsc.yaml"), catalog.Yaml);
        File.WriteAllText(Path.Combine(destination, "settings-profile.json"), SettingsProfileEmitter.RenderJson(profile));
        File.WriteAllText(Path.Combine(destination, "settings-profile.md"), SettingsProfileEmitter.RenderMarkdown(profile));
        File.WriteAllText(Path.Combine(destination, "README.md"), InDowsProfileReadme.Render(catalog, profile));

        var settingsManual = profile.Manual.Count + profile.PersoOnly.Count + profile.NotApplied.Sum(m => m.Settings.Count);
        return new AppsExportResult(
            destination,
            catalog.ActiveCount,
            catalog.CandidateCount + catalog.ManualCount,
            report.Readings.Count,
            settingsManual);
    }

    /// <summary>The settings catalog ships next to the app (like rules/ and modules/); fall back to the cwd.</summary>
    private static string ResolveSettingsDirectory()
    {
        var nextToExe = Path.Combine(AppContext.BaseDirectory, "settings");
        return Directory.Exists(nextToExe) ? nextToExe : "settings";
    }
}
