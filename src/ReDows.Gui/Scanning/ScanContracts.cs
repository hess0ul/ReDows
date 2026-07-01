using ReDows.Core.Scanning;

namespace ReDows.Gui.Scanning;

/// <summary>
/// What to scan. <see cref="FolderRoot"/> null = the whole PC (all in-scope volumes); else that subtree.
/// <see cref="CategoryModules"/> are the user's per-category choices (games, media…): each acts only
/// where the ruleset would REVIEW, so an empty list simply means "no category overrides".
/// </summary>
public sealed record ScanRequest(string? FolderRoot, IReadOnlyList<CategoryModule>? CategoryModules = null);

/// <summary>Live progress while a scan runs: how many items seen so far and the path currently being walked.</summary>
public sealed record ScanProgress(long Items, string CurrentPath);

/// <summary>One "export before the reset" alert (a DPAPI machine-bound capture rule matched).</summary>
public sealed record AlertRow(string Rule, string Items);

/// <summary>One head directory in the REVIEW work queue. <see cref="Bytes"/> is the raw size (for the review explorer).</summary>
public sealed record ReviewFolderRow(string Size, string Items, string Folder, long Bytes);

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
    IReadOnlyList<ReviewFolderRow> TopReview);

/// <summary>
/// Runs a scan off the UI thread. A seam: the real implementation drives the engine on this PC;
/// a test swaps a fake, so the scan view-model's states (running / done / interrupted / error) are
/// exercised without touching the machine. Honors the cancellation token; reports progress.
/// </summary>
public interface IScanRunner
{
    Task<ScanResultView> RunAsync(ScanRequest request, IProgress<ScanProgress> progress, CancellationToken cancellationToken);
}
