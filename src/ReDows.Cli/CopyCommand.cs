using System.Text;
using System.Text.Json;
using ReDows.Core.Backup;
using ReDows.Core.Scanning;
using ReDows.Providers.Windows;
using ReDows.Providers.Windows.Backup;

namespace ReDows.Cli;

/// <summary>
/// 'redows copy' — copy the CAPTURE files of a scan manifest to a destination the user picks
/// (local disk, USB key, or UNC network share). READ-ONLY on the source; every copied file is
/// verified by hash. capture:secret files are counted but not copied here — they go into the
/// encrypted vault (next step). Exit codes: 0 ok, 2 usage, 3 manifest missing/invalid, 4 error,
/// 5 completed with failures.
/// </summary>
public static class CopyCommand
{
    public static int Run(string[] options)
    {
        try
        {
            return RunCore(options);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Unexpected error: {ex.GetType().Name}: {ex.Message}");
            return 4;
        }
    }

    private static int RunCore(string[] options)
    {
        string? manifestPath = null;
        string? destination = null;
        string? vaultPassword = null;
        var noVss = false;

        for (var i = 0; i < options.Length; i++)
        {
            switch (options[i])
            {
                case "--manifest" when i + 1 < options.Length:
                    manifestPath = options[++i];
                    break;
                case "--to" when i + 1 < options.Length:
                    destination = options[++i];
                    break;
                case "--vault-password" when i + 1 < options.Length:
                    vaultPassword = options[++i];
                    break;
                case "--no-vss":
                    noVss = true;
                    break;
                default:
                    Console.Error.WriteLine($"Invalid option '{options[i]}'. Usage: redows copy --manifest <file.jsonl> --to <destination> [--vault-password <pw>] [--no-vss]");
                    return 2;
            }
        }

        if (manifestPath is null || destination is null)
        {
            Console.Error.WriteLine("Missing --manifest <file.jsonl> and/or --to <destination>. Usage: redows copy --manifest <file.jsonl> --to <destination>");
            return 2;
        }

        if (!File.Exists(manifestPath))
        {
            Console.Error.WriteLine($"Manifest not found: '{manifestPath}'. Produce one first with 'redows scan --manifest <file.jsonl>'.");
            return 3;
        }

        var fullDestination = Path.GetFullPath(destination);
        Directory.CreateDirectory(fullDestination);

        Console.Error.WriteLine($"Copying the manifest's CAPTURE files to '{fullDestination}' (read-only on the source)…");
        if (vaultPassword is null)
        {
            Console.Error.WriteLine("  (no --vault-password: capture:secret files will be counted and deferred, never copied in clear.)");
        }

        // Locked-file rescue: on an elevated run we read a locked file from a volume shadow copy
        // (a frozen snapshot) so it is captured, not lost. The shadow is only made if a lock is
        // actually hit, so an unlocked run costs nothing. --no-vss opts out; no admin means we
        // report the locked files instead of rescuing them.
        var elevated = Elevation.IsElevated();
        var useVss = !noVss && elevated;
        AnnounceVssMode(noVss, elevated);

        ShadowCopySource? shadow = useVss
            ? new ShadowCopySource(new FileSystemCopySource(), new WmiVolumeSnapshotSet())
            : null;
        ICopySource copySource = shadow ?? (ICopySource)new FileSystemCopySource();

        string? vaultPath = vaultPassword is null ? null : Path.Combine(fullDestination, "secrets-vault.zip");
        ZipVaultSink? vault = vaultPath is null
            ? null
            : new ZipVaultSink(new FileStream(vaultPath, FileMode.Create, FileAccess.Write, FileShare.None), vaultPassword!);

        CopyReport report;
        long? rescued = null;
        try
        {
            report = CopyEngine.Run(
                ReadManifest(manifestPath),
                copySource,
                new FileSystemSink(fullDestination),
                vault,
                onProgress: (items, path) => Console.Error.WriteLine($"  … {items:N0} entries — {Sanitize(path)}"));
            rescued = shadow?.RescuedPaths.Count;
        }
        finally
        {
            vault?.Dispose();  // finalize the encrypted zip before verifying it
            shadow?.Dispose(); // delete any volume shadow copies we created
        }

        // Record each copied file's checksum, so a later restore can verify end-to-end that what it puts
        // back is byte-identical to the original — even if the backup medium degraded in between.
        if (report.Hashes.Count > 0)
        {
            WriteHashManifest(fullDestination, report.Hashes);
        }

        var vaultStatus = VerifyVault(vaultPath, vaultPassword, report.SecretsVaulted);

        Console.WriteLine(Render(report, fullDestination, vaultStatus, rescued));
        return report.Failures.Count > 0 ? 5 : 0;
    }

    /// <summary>Say, on stderr, how locked files will be handled this run — never leave it implicit.</summary>
    private static void AnnounceVssMode(bool noVss, bool elevated)
    {
        if (noVss)
        {
            Console.Error.WriteLine("  --no-vss: shadow-copy rescue disabled; locked files will be reported, not copied.");
        }
        else if (elevated)
        {
            Console.Error.WriteLine("  locked files will be rescued from a volume shadow copy (running elevated).");
        }
        else
        {
            Console.Error.WriteLine("  not elevated: locked files cannot be rescued (they will be reported) — re-run as administrator to capture them.");
        }
    }

    /// <summary>Re-open the finished vault and read every entry back: prove it opens and is intact.</summary>
    private static string? VerifyVault(string? vaultPath, string? vaultPassword, long expected)
    {
        if (vaultPath is null || vaultPassword is null)
        {
            return null;
        }

        try
        {
            var verified = ZipVaultSink.Verify(vaultPath, vaultPassword);
            return verified == expected
                ? $"vault OK — {verified:N0} secret(s) encrypted and verified ('{Path.GetFileName(vaultPath)}')"
                : $"vault MISMATCH — verified {verified:N0} but vaulted {expected:N0} ✗";
        }
        catch (Exception ex)
        {
            return $"vault verify FAILED: {ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>Write the per-file checksum manifest (SHA-256) at the destination root, for restore verification.</summary>
    private static void WriteHashManifest(string destination, IReadOnlyList<FileHash> hashes)
    {
        var json = JsonSerializer.Serialize(
            new { version = 1, algorithm = "SHA-256", files = hashes.Select(h => new { path = h.RelativePath, sha256 = h.Sha256 }) },
            new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        File.WriteAllText(Path.Combine(destination, "redows-hashes.json"), json);
    }

    /// <summary>Stream the JSONL manifest line by line (it can be large): blank/bad lines are skipped.</summary>
    private static IEnumerable<ManifestEntry> ReadManifest(string path)
    {
        foreach (var line in File.ReadLines(path))
        {
            if (ManifestLine.Parse(line) is { } entry)
            {
                yield return entry;
            }
        }
    }

    private static string Render(CopyReport report, string destination, string? vaultStatus, long? rescuedFromShadow)
    {
        var text = new StringBuilder();
        text.AppendLine("== ReDows copy ==");
        text.AppendLine($"destination: {destination}");
        text.AppendLine();
        text.AppendLine($"  files copied & verified : {report.FilesCopied,12:N0}  {Bytes(report.BytesCopied)}");
        if (rescuedFromShadow is { } rescued)
        {
            text.AppendLine($"    of which locked→shadow: {rescued,12:N0}  (read from a frozen volume snapshot)");
        }

        text.AppendLine($"  directories (structure) : {report.Directories,12:N0}");
        text.AppendLine($"  secrets → encrypted vault: {report.SecretsVaulted,11:N0}  {Bytes(report.SecretBytesVaulted)}");
        text.AppendLine($"  secrets deferred (no pw): {report.SecretsDeferred,12:N0}  {Bytes(report.SecretBytesDeferred)}");
        text.AppendLine($"  failed                  : {report.Failures.Count,12:N0}");
        text.AppendLine(
            $"  equation: {report.FilesCopied} + {report.Directories} + {report.SecretsVaulted} + {report.SecretsDeferred} + {report.Failures.Count} " +
            $"= {report.Accounted:N0} accounted vs {report.TotalEntries:N0} entries → " +
            (report.Unaccounted == 0 ? "0 unaccounted ✓" : $"{report.Unaccounted:N0} UNACCOUNTED ✗ (engine bug)"));
        if (vaultStatus is not null)
        {
            text.AppendLine($"  {vaultStatus}");
        }

        if (report.Failures.Count > 0)
        {
            text.AppendLine();
            text.AppendLine($"== Failed ({report.Failures.Count}) — reported, never silently dropped ==");
            foreach (var failure in report.Failures.Take(50))
            {
                text.AppendLine($"  {Sanitize(failure.Path)} — {Sanitize(failure.Reason)}");
            }

            if (report.Failures.Count > 50)
            {
                text.AppendLine($"  … and {report.Failures.Count - 50:N0} more.");
            }
        }

        text.AppendLine();
        text.AppendLine("== Declared limits (V1) ==");
        foreach (var limit in CopyReport.V1Limits)
        {
            text.AppendLine($"  - {limit}");
        }

        return text.ToString();
    }

    private static string Sanitize(string text) => ConsoleText.Sanitize(text);

    private static string Bytes(long bytes) => bytes switch
    {
        >= 1L << 40 => $"{bytes / (double)(1L << 40):F2} TB",
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F2} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F2} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F1} KB",
        _ => $"{bytes} B",
    };
}
