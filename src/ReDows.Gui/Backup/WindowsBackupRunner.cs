using System.IO;
using System.Text.Json;
using ReDows.Core.Backup;
using ReDows.Core.Duplicates;
using ReDows.Core.Rules;
using ReDows.Core.Scanning;
using ReDows.Providers.Windows;
using ReDows.Providers.Windows.Backup;

namespace ReDows.Gui.Backup;

/// <summary>
/// The real backup: drives the same <see cref="CopyEngine"/> the CLI's 'redows copy' uses, on a
/// background thread. Read-only on the source; each file is hash-verified; capture:secret files go
/// only into the encrypted vault (never in clear); locked files are rescued from a volume shadow copy
/// when elevated. Cancellation is injected by stopping the manifest enumeration (the engine takes no
/// token): a cancelled run throws <see cref="OperationCanceledException"/> and leaves the partial copy.
/// </summary>
public sealed class WindowsBackupRunner : IBackupRunner
{
    public bool IsElevated => Elevation.IsElevated();

    public Task<BackupResultView> RunAsync(BackupRequest request, IProgress<BackupProgress> progress, CancellationToken cancellationToken) =>
        Task.Run(() => RunCore(request, progress, cancellationToken), cancellationToken);

    private static BackupResultView RunCore(BackupRequest request, IProgress<BackupProgress> progress, CancellationToken cancellationToken)
    {
        if (!File.Exists(request.ManifestPath))
        {
            throw new InvalidOperationException("There is nothing to back up yet — run a scan first, then come back here.");
        }

        var destination = Path.GetFullPath(request.Destination);
        Directory.CreateDirectory(destination);

        // Locked-file rescue via VSS only works elevated; otherwise locked files are reported (never
        // silently skipped). The shadow is made on demand only if a lock is actually hit.
        var useVss = request.UseVss && Elevation.IsElevated();
        ShadowCopySource? shadow = useVss
            ? new ShadowCopySource(new FileSystemCopySource(), new WmiVolumeSnapshotSet())
            : null;
        ICopySource source = shadow ?? (ICopySource)new FileSystemCopySource();

        // A password → an encrypted vault next to the copy; no password → secrets counted and deferred,
        // never written in clear (invariant #5).
        var vaultPath = string.IsNullOrEmpty(request.VaultPassword)
            ? null
            : Path.Combine(destination, "secrets-vault.zip");
        ZipVaultSink? vault = vaultPath is null
            ? null
            : new ZipVaultSink(new FileStream(vaultPath, FileMode.Create, FileAccess.Write, FileShare.None), request.VaultPassword!);

        // The review trash removes REVIEW items the user dropped in the sorter (never a CAPTURE/secret).
        var trashed = request.ExcludedPaths ?? [];
        long excludedItems = 0, excludedBytes = 0;

        // Optional de-duplication pre-pass: find byte-identical files across the plain-copy set (never
        // secrets), so only the most-recent copy is written and the other places it belongs are recorded
        // in a restore map. Read-only. A file that cannot be hashed is simply treated as unique (copied).
        var plan = request.Dedupe ? BuildDedupePlan(request.ManifestPath, trashed, progress, cancellationToken) : null;

        CopyReport report;
        long? rescued = null;
        try
        {
            report = CopyEngine.Run(
                ReadManifest(request.ManifestPath, trashed, plan?.SkipPaths, cancellationToken, entry => { excludedItems++; excludedBytes += entry.Bytes; }),
                source,
                new FileSystemSink(destination),
                vault,
                onProgress: (items, path) => progress.Report(new BackupProgress(items, path)));
            rescued = shadow?.RescuedPaths.Count;
        }
        finally
        {
            vault?.Dispose();  // finalize the encrypted zip before verifying it
            shadow?.Dispose(); // delete any volume shadow copies we created
        }

        // Record where each de-duplicated content belongs, so a later restore can replicate it.
        if (plan is { Map.Count: > 0 })
        {
            WriteRestoreMap(destination, plan.Map);
        }

        // Record each copied file's checksum, so a later restore can verify end-to-end that what it puts
        // back is byte-identical to the original — catching a backup medium that degraded in between.
        if (report.Hashes.Count > 0)
        {
            WriteHashManifest(destination, report.Hashes);
        }

        var vaultStatus = VerifyVault(vaultPath, request.VaultPassword, report.SecretsVaulted);
        var excludedText = excludedItems > 0 ? $"{excludedItems:N0} items · {Format.Bytes(excludedBytes)}" : null;
        var dedupeText = plan is null
            ? null
            : plan.DedupedItems > 0
                ? $"{plan.DedupedItems:N0} duplicate files stored once · {Format.Bytes(plan.SavedBytes)} saved (see redows-restore-map.json)"
                : "no duplicate files found";
        return Shape(report, vaultStatus, rescued) with { ExcludedText = excludedText, DedupeText = dedupeText };
    }

    /// <summary>
    /// De-duplication pre-pass: hash the plain-copy files (never secrets, never directories, and not the
    /// trashed items) and group the byte-identical ones, so the copy stores each content once. Reuses the
    /// same <see cref="DuplicateFinder"/> as the scan's duplicate tool.
    /// </summary>
    private static BackupDedupePlan BuildDedupePlan(
        string manifestPath, IReadOnlyList<string> trashed, IProgress<BackupProgress> progress, CancellationToken cancellationToken)
    {
        var files = new List<FileRef>();
        foreach (var line in File.ReadLines(manifestPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ManifestLine.Parse(line) is { } entry && IsPlainCopy(entry) && !BackupSelection.IsTrashed(entry, trashed))
            {
                files.Add(new FileRef(entry.Path, entry.Bytes));
            }
        }

        long hashed = 0;
        var groups = DuplicateFinder.Find(files, new Sha256FileHasher(), SafeLastModifiedUtc, minSize: 1, onFullHash: _ =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++hashed % 500 == 0)
            {
                progress.Report(new BackupProgress(hashed, "looking for duplicate files to store once…"));
            }
        });

        return BackupDedupePlan.Build(groups);
    }

    private static readonly string SecretVerdict = Verdict.CaptureSecret.Format();

    /// <summary>A file the copy writes in clear (not a directory, not a secret — secrets go to the vault).</summary>
    private static bool IsPlainCopy(ManifestEntry entry) =>
        !entry.IsDirectory && !string.Equals(entry.Verdict, SecretVerdict, StringComparison.OrdinalIgnoreCase);

    private static DateTime SafeLastModifiedUtc(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path.Replace('/', '\\'));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>Write the de-duplication restore map at the destination root (human-readable JSON).</summary>
    private static void WriteRestoreMap(string destination, IReadOnlyList<RestoreMapEntry> map)
    {
        var json = JsonSerializer.Serialize(
            new { version = 1, duplicates = map },
            new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        File.WriteAllText(Path.Combine(destination, "redows-restore-map.json"), json);
    }

    /// <summary>Write the per-file checksum manifest at the destination root (SHA-256, for restore verification).</summary>
    private static void WriteHashManifest(string destination, IReadOnlyList<FileHash> hashes)
    {
        var json = JsonSerializer.Serialize(
            new { version = 1, algorithm = "SHA-256", files = hashes.Select(h => new { path = h.RelativePath, sha256 = h.Sha256 }) },
            new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        File.WriteAllText(Path.Combine(destination, "redows-hashes.json"), json);
    }

    /// <summary>
    /// Stream the JSONL manifest, dropping the review items the user trashed (counted via
    /// <paramref name="onExcluded"/>, never silently) and the duplicate copies de-duplication stores
    /// elsewhere (<paramref name="dedupeSkip"/>). Cancellation is honored between lines (the engine takes
    /// no token).
    /// </summary>
    private static IEnumerable<ManifestEntry> ReadManifest(
        string path, IReadOnlyList<string> trashed, IReadOnlySet<string>? dedupeSkip,
        CancellationToken cancellationToken, Action<ManifestEntry> onExcluded)
    {
        foreach (var line in File.ReadLines(path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ManifestLine.Parse(line) is not { } entry)
            {
                continue;
            }

            if (BackupSelection.IsTrashed(entry, trashed))
            {
                onExcluded(entry);
                continue;
            }

            if (dedupeSkip is not null && dedupeSkip.Contains(entry.Path))
            {
                continue; // an identical copy is stored once via its most-recent twin (in the restore map)
            }

            yield return entry;
        }
    }

    /// <summary>Re-open the finished vault and read every entry back: prove it opens and is intact.</summary>
    private static string? VerifyVault(string? vaultPath, string? vaultPassword, long expected)
    {
        if (vaultPath is null || string.IsNullOrEmpty(vaultPassword))
        {
            return null;
        }

        try
        {
            var verified = ZipVaultSink.Verify(vaultPath, vaultPassword);
            return verified == expected
                ? $"vault OK — {verified:N0} secret(s) encrypted and verified ({Path.GetFileName(vaultPath)})"
                : $"vault MISMATCH — verified {verified:N0} but vaulted {expected:N0}";
        }
        catch (Exception ex)
        {
            return $"vault verify FAILED: {ex.GetType().Name}: {ex.Message}";
        }
    }

    private static BackupResultView Shape(CopyReport report, string? vaultStatus, long? rescued)
    {
        var equation =
            $"{report.FilesCopied} + {report.Directories} + {report.SecretsVaulted} + {report.SecretsDeferred} + {report.Failures.Count} " +
            $"= {report.Accounted:N0} accounted vs {report.TotalEntries:N0} entries → " +
            (report.Unaccounted == 0 ? "0 unaccounted" : $"{report.Unaccounted:N0} UNACCOUNTED (bug)");

        return new BackupResultView(
            Balanced: report.Unaccounted == 0,
            CopiedText: $"{report.FilesCopied:N0} files · {Format.Bytes(report.BytesCopied)}",
            RescuedText: rescued is > 0 ? $"{rescued:N0} rescued from a volume shadow copy (were locked)" : null,
            DirectoriesText: $"{report.Directories:N0}",
            VaultedText: $"{report.SecretsVaulted:N0} secrets · {Format.Bytes(report.SecretBytesVaulted)}",
            DeferredText: $"{report.SecretsDeferred:N0} secrets · {Format.Bytes(report.SecretBytesDeferred)}",
            FailedText: $"{report.Failures.Count:N0}",
            EquationText: equation,
            VaultStatus: vaultStatus,
            TopFailures: report.Failures.Take(25).Select(f => new BackupFailureRow(f.Path, f.Reason)).ToList());
    }
}
