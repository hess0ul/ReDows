using System.IO;
using ReDows.Core.Backup;
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

        CopyReport report;
        long? rescued = null;
        try
        {
            report = CopyEngine.Run(
                ReadManifest(request.ManifestPath, cancellationToken),
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

        var vaultStatus = VerifyVault(vaultPath, request.VaultPassword, report.SecretsVaulted);
        return Shape(report, vaultStatus, rescued);
    }

    /// <summary>Stream the JSONL manifest; cancellation is honored between lines (the engine takes no token).</summary>
    private static IEnumerable<ManifestEntry> ReadManifest(string path, CancellationToken cancellationToken)
    {
        foreach (var line in File.ReadLines(path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ManifestLine.Parse(line) is { } entry)
            {
                yield return entry;
            }
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
