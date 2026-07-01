namespace ReDows.Gui.Restore;

/// <summary>
/// A restore to run — the inverse of a backup. <see cref="BackupFolder"/> is a folder produced by the
/// Backup screen (self-describing: the file tree + redows-restore-map.json + secrets-vault.zip).
/// <see cref="ToOriginalLocations"/> true = put files back where they came from; false = reconstruct the
/// tree under <see cref="TargetFolder"/> instead (safe, you move things yourself). <see cref="VaultPassword"/>
/// unlocks the secrets vault so secrets are restored too; null = leave the vault for the user to open.
/// NON-DESTRUCTIVE: existing files are skipped, never overwritten or deleted.
/// </summary>
public sealed record RestoreRequest(string BackupFolder, bool ToOriginalLocations, string? TargetFolder, string? VaultPassword);

/// <summary>Live progress while a restore runs: how many backup files processed, and the current one.</summary>
public sealed record RestoreProgress(long Items, string CurrentPath);

/// <summary>One target that could not be written — surfaced, never silently dropped.</summary>
public sealed record RestoreFailureRow(string Path, string Reason);

/// <summary>
/// A restore result shaped for friendly display. <see cref="SecretsText"/> always describes what happened
/// to the secrets vault (absent / restored / left for the user / failed). Skipped = a file already existed
/// and was kept as-is.
/// </summary>
public sealed record RestoreResultView(
    string RestoredText,
    string SkippedText,
    string FailedText,
    string SecretsText,
    IReadOnlyList<RestoreFailureRow> TopFailures);

/// <summary>
/// Runs a restore off the UI thread. A seam: the real implementation writes to this PC; a test swaps a
/// fake to exercise the view-model's states without touching disk. Honors the cancellation token; reports
/// progress. It WRITES to the target (that is the point of a restore) but never deletes or overwrites.
/// </summary>
public interface IRestoreRunner
{
    Task<RestoreResultView> RunAsync(RestoreRequest request, IProgress<RestoreProgress> progress, CancellationToken cancellationToken);
}
