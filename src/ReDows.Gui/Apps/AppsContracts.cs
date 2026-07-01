using ReDows.Core.Apps;

namespace ReDows.Gui.Apps;

/// <summary>The installed apps to show, plus a one-line summary (how many, how many auto-reinstallable).</summary>
public sealed record AppsLoadResult(IReadOnlyList<AppEntry> Apps, string Summary);

/// <summary>
/// The outcome of writing the InDows reinstall profile. <see cref="Path"/> is the configuration.dsc.yaml
/// file InDows reads at first logon. <see cref="ActiveCount"/> apps have a confirmed winget id and reinstall
/// automatically; the others are listed as comments to review (never dropped, never wrongly installed).
/// </summary>
public sealed record AppsExportResult(string Path, int ActiveCount, int CandidateCount, int ManualCount);

/// <summary>
/// Reads the installed-apps inventory (the same one 'redows apps' uses) and writes the InDows reinstall
/// profile from the user's selection. A seam: the real implementation touches the machine; a test swaps a
/// fake so the Apps view-model is exercised without reading the registry. Loading runs off the UI thread
/// (winget enrichment can take a few seconds); the export is a small, fast file write.
/// </summary>
public interface IAppsRunner
{
    Task<AppsLoadResult> LoadAsync(bool enrichWithWinget, IProgress<string> progress, CancellationToken cancellationToken);

    /// <summary>Emit configuration.dsc.yaml (winget DSC) for the chosen apps — the apps half of ReDows → InDows.</summary>
    AppsExportResult Export(IReadOnlyList<AppEntry> selectedApps, string folder);
}
