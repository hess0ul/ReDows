using ReDows.Core.Apps;

namespace ReDows.Gui.Apps;

/// <summary>The installed apps to show, plus a one-line summary (how many, how many auto-reinstallable).</summary>
public sealed record AppsLoadResult(IReadOnlyList<AppEntry> Apps, string Summary);

/// <summary>
/// The outcome of writing the full InDows profile folder (configuration.dsc.yaml + settings-profile.json/.md
/// + README.md). <see cref="Folder"/> is where it was written. <see cref="AppsActive"/> apps have a confirmed
/// winget id and reinstall automatically; <see cref="AppsCommented"/> are listed as comments to review.
/// <see cref="SettingsRead"/> Windows settings were captured; <see cref="SettingsManual"/> of them need a
/// manual touch after the reset (no module restores them). Nothing is ever dropped.
/// </summary>
public sealed record AppsExportResult(string Folder, int AppsActive, int AppsCommented, int SettingsRead, int SettingsManual);

/// <summary>
/// Reads the installed-apps inventory (the same one 'redows apps' uses) and writes the FULL InDows profile
/// from the user's selection (apps + Windows settings + README). A seam: the real implementation touches the
/// machine; a test swaps a fake so the Apps view-model is exercised without reading the registry. Both calls
/// run off the UI thread — loading winget-enriches (a few seconds) and the export reads the live settings.
/// Read-only on the machine; only writes the profile folder the user picks. Never reads a secret value.
/// </summary>
public interface IAppsRunner
{
    Task<AppsLoadResult> LoadAsync(bool enrichWithWinget, IProgress<string> progress, CancellationToken cancellationToken);

    /// <summary>Write the full InDows profile (apps catalog + settings + README) for the chosen apps.</summary>
    Task<AppsExportResult> ExportProfileAsync(IReadOnlyList<AppEntry> selectedApps, string folder, IProgress<string> progress, CancellationToken cancellationToken);
}
