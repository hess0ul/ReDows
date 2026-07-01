using ReDows.Core.Scanning;

namespace ReDows.Gui.Scanning;

/// <summary>
/// What to scan. <see cref="FolderRoot"/> null = the whole PC (all in-scope volumes); else that subtree.
/// <see cref="CategoryModules"/> are the user's per-category choices (games, media…): each acts only
/// where the ruleset would REVIEW, so an empty list simply means "no category overrides".
/// <see cref="Duplicates"/> null = don't hunt duplicates.
/// <see cref="RecognizeInstalledApps"/> true (default, mirrors the CLI's on-by-default) = feed the
/// app inventory into the scan so recognised install folders are treated as re-downloadable (ignored
/// where the ruleset would only review) and each app's settings are kept — the CLI's <c>--no-reinstall</c>
/// off-switch, exposed as a checkbox.
/// </summary>
public sealed record ScanRequest(
    string? FolderRoot,
    IReadOnlyList<CategoryModule>? CategoryModules = null,
    DuplicateScan? Duplicates = null,
    bool RecognizeInstalledApps = true);

/// <summary>
/// Whether to also find byte-identical files, and — for a per-type run — the lower-cased extensions
/// (no dot) to limit the hunt to. <see cref="Extensions"/> null = every file (the "global" mode);
/// a list = only files with one of those extensions (the "per type" mode). Read-only: this only
/// reports duplicates and the space one copy each would free — nothing is ever deleted.
/// </summary>
public sealed record DuplicateScan(bool Enabled, IReadOnlyList<string>? Extensions);

/// <summary>Live progress while a scan runs: how many items seen so far and the path currently being walked.</summary>
public sealed record ScanProgress(long Items, string CurrentPath);

/// <summary>One "export before the reset" alert (a DPAPI machine-bound capture rule matched).</summary>
public sealed record AlertRow(string Rule, string Items);

/// <summary>One head directory in the REVIEW work queue. <see cref="Bytes"/> is the raw size (for the review explorer).</summary>
public sealed record ReviewFolderRow(string Size, string Items, string Folder, long Bytes);

/// <summary>
/// One duplicate group, shaped for display: <see cref="KeepPath"/> is the copy to keep (the most
/// recently modified "truth"), <see cref="OtherPaths"/> the other places the identical content lives.
/// </summary>
public sealed record DuplicateGroupRow(string Reclaimable, string SizeEach, int Copies, string KeepPath, IReadOnlyList<string> OtherPaths);

/// <summary>Duplicate-file findings for the Scan screen: how many groups, the space one copy each would free, and the biggest.</summary>
public sealed record DuplicateSummary(int Groups, int ExtraCopies, string Reclaimable, IReadOnlyList<DuplicateGroupRow> Top);

/// <summary>
/// What recognizing this PC's installed apps did to the scan — the same accounting the CLI prints,
/// shaped for display. <see cref="Apps"/> = how many apps the inventory found. <see cref="IgnoredText"/>
/// = install folders treated as re-downloadable and moved out of REVIEW into IGNORE. <see cref="KeptText"/>
/// = each app's %AppData% config captured. <see cref="SurfacedText"/> = each app's %LocalAppData%
/// (mixed config + cache) surfaced for REVIEW. Null on the result when the option was off. All read-only
/// accounting: recognition only ever acts where the ruleset would REVIEW — never over a keep or a secret.
/// </summary>
public sealed record InstalledAppsImpact(int Apps, string IgnoredText, string KeptText, string SurfacedText);

/// <summary>
/// A scan result shaped for friendly display (formatted strings, not raw records) — what the
/// Scan screen shows. <see cref="Partial"/> = the run was interrupted (Cancel), so figures cover
/// only what was walked; <see cref="Balanced"/> = the total-accounting equation held (0 unaccounted).
/// </summary>
public sealed record ScanResultView(
    bool Partial,
    string KeepText,
    string ReviewText,
    string IgnoreText,
    string NoteText,
    bool Balanced,
    string EquationText,
    IReadOnlyList<AlertRow> Alerts,
    IReadOnlyList<ReviewFolderRow> TopReview,
    DuplicateSummary? Duplicates = null,
    InstalledAppsImpact? InstalledApps = null);

/// <summary>
/// Runs a scan off the UI thread. A seam: the real implementation drives the engine on this PC;
/// a test swaps a fake, so the scan view-model's states (running / done / interrupted / error) are
/// exercised without touching the machine. Honors the cancellation token; reports progress.
/// </summary>
public interface IScanRunner
{
    Task<ScanResultView> RunAsync(ScanRequest request, IProgress<ScanProgress> progress, CancellationToken cancellationToken);
}
