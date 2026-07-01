namespace ReDows.Gui.Backup;

/// <summary>
/// A backup to run: copy the CAPTURE files listed in <see cref="ManifestPath"/> to <see cref="Destination"/>
/// (a folder, USB key, or UNC share). <see cref="VaultPassword"/> null = no password, so secrets are
/// counted and deferred, never copied in clear; a password puts them in an encrypted vault instead.
/// <see cref="UseVss"/> = try to rescue locked files from a volume shadow copy (only takes effect when
/// the app runs elevated). <see cref="ExcludedPaths"/> = the review trash (paths the user dropped in the
/// sorter): a REVIEW item under one of these is skipped, but a CAPTURE item is never dropped by the trash.
/// Read-only on the source; every copied file is verified by hash.
/// </summary>
public sealed record BackupRequest(
    string ManifestPath,
    string Destination,
    string? VaultPassword,
    bool UseVss,
    IReadOnlyList<string>? ExcludedPaths = null);

/// <summary>Live progress while a backup runs: how many manifest entries processed, and the current path.</summary>
public sealed record BackupProgress(long Items, string CurrentPath);

/// <summary>One manifest entry that could not be copied — surfaced, never silently dropped.</summary>
public sealed record BackupFailureRow(string Path, string Reason);

/// <summary>
/// A copy result shaped for friendly display. <see cref="Balanced"/> = the total-accounting equation
/// held (every entry landed in exactly one bucket). <see cref="RescuedText"/> and <see cref="VaultStatus"/>
/// are null when they don't apply (no locked files rescued / no vault).
/// </summary>
public sealed record BackupResultView(
    bool Balanced,
    string CopiedText,
    string? RescuedText,
    string DirectoriesText,
    string VaultedText,
    string DeferredText,
    string FailedText,
    string EquationText,
    string? VaultStatus,
    IReadOnlyList<BackupFailureRow> TopFailures,
    string? ExcludedText = null);

/// <summary>
/// Runs a backup off the UI thread. A seam: the real implementation drives <c>CopyEngine</c> on this PC
/// (the same engine the CLI's 'redows copy' uses); a test swaps a fake to exercise the view-model's
/// states without touching the disk. <see cref="IsElevated"/> reports whether locked-file rescue (VSS)
/// can actually work this run. Honors the cancellation token; reports progress.
/// </summary>
public interface IBackupRunner
{
    bool IsElevated { get; }

    Task<BackupResultView> RunAsync(BackupRequest request, IProgress<BackupProgress> progress, CancellationToken cancellationToken);
}
