using System.Security.Cryptography;
using ReDows.Core.Rules;
using ReDows.Core.Scanning;

namespace ReDows.Core.Backup;

/// <summary>
/// Copies the CAPTURE items of a manifest from a read-only <see cref="ICopySource"/> into
/// an <see cref="IBackupSink"/>, verifying each file by hash. All I/O is behind the two
/// interfaces, so the whole engine is testable on fakes. Routing (step 1 of the copy block):
/// directories are skipped (recreated implicitly), <c>capture:secret</c> files are deferred
/// to the encrypted vault, everything else is copied and verified.
/// </summary>
public static class CopyEngine
{
    public static CopyReport Run(
        IEnumerable<ManifestEntry> entries,
        ICopySource source,
        IBackupSink sink,
        IVaultSink? vault = null,
        Action<long, string>? onProgress = null,
        long progressInterval = 500)
    {
        long total = 0, copied = 0, copiedBytes = 0, verified = 0, directories = 0;
        long vaulted = 0, vaultedBytes = 0, deferred = 0, deferredBytes = 0;
        var failures = new List<CopyFailure>();

        foreach (var entry in entries)
        {
            total++;

            if (entry.IsDirectory)
            {
                directories++;
            }
            else if (string.Equals(entry.Verdict, SecretVerdict, StringComparison.OrdinalIgnoreCase))
            {
                // capture:secret → the encrypted vault. Without a vault (no password) it is
                // deferred (counted), never copied in clear (invariant #5).
                if (vault is null)
                {
                    deferred++;
                    deferredBytes += entry.Bytes;
                }
                else if (VaultOne(entry, source, vault) is { } vaultFailure)
                {
                    failures.Add(vaultFailure);
                }
                else
                {
                    vaulted++;
                    vaultedBytes += entry.Bytes;
                }
            }
            else if (CopyOne(entry, source, sink) is { } failure)
            {
                failures.Add(failure);
            }
            else
            {
                copied++;
                copiedBytes += entry.Bytes;
                verified++;
            }

            if (onProgress is not null && progressInterval > 0 && total % progressInterval == 0)
            {
                onProgress(total, entry.Path);
            }
        }

        return new CopyReport(
            total, copied, copiedBytes, verified, directories, vaulted, vaultedBytes, deferred, deferredBytes, failures);
    }

    /// <summary>Stream one secret file into the encrypted vault; returns a failure or null.</summary>
    private static CopyFailure? VaultOne(ManifestEntry entry, ICopySource source, IVaultSink vault)
    {
        try
        {
            using var input = source.OpenRead(entry.Path);
            vault.Add(RelativePath(entry.Path), input);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new CopyFailure(entry.Path, ex.GetType().Name);
        }
    }

    private static readonly string SecretVerdict = Verdict.CaptureSecret.Format();

    /// <summary>Copy one file and verify it; returns a failure or null on success.</summary>
    private static CopyFailure? CopyOne(ManifestEntry entry, ICopySource source, IBackupSink sink)
    {
        var relativePath = RelativePath(entry.Path);
        try
        {
            string sourceHash;
            using (var input = source.OpenRead(entry.Path))
            using (var output = sink.OpenWrite(relativePath))
            {
                sourceHash = CopyAndHash(input, output);
            }

            string destinationHash;
            using (var readBack = sink.OpenReadBack(relativePath))
            {
                destinationHash = Hash(readBack);
            }

            return string.Equals(sourceHash, destinationHash, StringComparison.OrdinalIgnoreCase)
                ? null
                : new CopyFailure(entry.Path, "verification mismatch (destination differs from source)");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new CopyFailure(entry.Path, ex.GetType().Name);
        }
    }

    /// <summary>
    /// Map a normalized source path to a destination-relative path that preserves the tree
    /// and never collides across volumes: the volume's ':' becomes a folder
    /// (<c>C:/Users/x</c> → <c>C/Users/x</c>).
    /// </summary>
    public static string RelativePath(string sourcePath) =>
        string.Join('/', ScanPaths.Split(sourcePath).Select(segment => segment.Replace(":", "", StringComparison.Ordinal)));

    private static string CopyAndHash(Stream input, Stream output)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            output.Write(buffer, 0, read);
            hash.AppendData(buffer, 0, read);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string Hash(Stream stream)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            hash.AppendData(buffer, 0, read);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
