using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using ReDows.Gui.Backup;
using ReDows.Providers.Windows.Backup;

namespace ReDows.Gui.Restore;

/// <summary>
/// The real restore: walks a backup folder and puts every file back — to its original location or under a
/// chosen folder — replicating de-duplicated content to all the places it belonged (via the restore map)
/// and extracting the encrypted secrets vault when a password is given. Runs on a background thread,
/// honoring cancellation. NON-DESTRUCTIVE: a file that already exists is skipped (kept), never overwritten,
/// and nothing is ever deleted.
/// </summary>
public sealed class WindowsRestoreRunner : IRestoreRunner
{
    private const string RestoreMapName = "redows-restore-map.json";
    private const string HashManifestName = "redows-hashes.json";
    private const string VaultName = "secrets-vault.zip";

    public Task<RestoreResultView> RunAsync(RestoreRequest request, IProgress<RestoreProgress> progress, CancellationToken cancellationToken) =>
        Task.Run(() => RunCore(request, progress, cancellationToken), cancellationToken);

    private static RestoreResultView RunCore(RestoreRequest request, IProgress<RestoreProgress> progress, CancellationToken cancellationToken)
    {
        var backup = Path.GetFullPath(request.BackupFolder);
        if (!Directory.Exists(backup))
        {
            // A network share (\\server\share) that isn't there usually means the share is unreachable
            // (server off, not signed in) rather than a wrong path — say so, so a NAS restore fails clearly.
            throw new InvalidOperationException(IsNetworkPath(backup)
                ? $"Could not reach the network backup folder '{backup}'. Is the share available — server on, and are you signed in to it?"
                : $"That backup folder does not exist: '{backup}'.");
        }

        var targetFolder = request.ToOriginalLocations ? null : NormalizeTargetFolder(request.TargetFolder);
        var plan = new RestorePlan(LoadRestoreMap(Path.Combine(backup, RestoreMapName)), request.ToOriginalLocations, targetFolder);
        var expectedHashes = LoadHashManifest(Path.Combine(backup, HashManifestName));

        long restored = 0, restoredBytes = 0, skipped = 0, verified = 0, seen = 0;
        var failures = new List<RestoreFailureRow>();

        foreach (var file in Directory.EnumerateFiles(backup, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(backup, file).Replace('\\', '/');
            if (IsSpecialFile(rel))
            {
                continue; // the restore map / hashes / vault are ReDows' own outputs, not user files to place
            }

            expectedHashes.TryGetValue(rel, out var expected);
            foreach (var target in plan.TargetsFor(rel))
            {
                var outcome = WriteFile(() => File.OpenRead(file), target);
                if (outcome.Skipped)
                {
                    skipped++;
                }
                else if (outcome.Reason is { } reason)
                {
                    failures.Add(new RestoreFailureRow(target, reason));
                }
                else if (expected is not null && !FileMatches(target, expected))
                {
                    // Written, but it does not match the checksum recorded at backup time — surface it,
                    // never a silent success. The (bad) file is left in place (non-destructive).
                    failures.Add(new RestoreFailureRow(target, "checksum mismatch — restored file does not match the backup"));
                }
                else
                {
                    restored++;
                    restoredBytes += outcome.Bytes;
                    if (expected is not null)
                    {
                        verified++;
                    }
                }
            }

            if (++seen % 200 == 0)
            {
                progress.Report(new RestoreProgress(seen, rel));
            }
        }

        var secretsText = RestoreVault(backup, request, plan, cancellationToken);

        return new RestoreResultView(
            RestoredText: $"{restored:N0} files · {Format.Bytes(restoredBytes)}",
            SkippedText: $"{skipped:N0} already existed (kept as-is)",
            FailedText: $"{failures.Count:N0}",
            SecretsText: secretsText,
            TopFailures: failures.Take(25).ToList(),
            VerifiedText: expectedHashes.Count > 0 ? $"{verified:N0} checksum-verified against the backup" : null);
    }

    /// <summary>Extract the secrets vault to its targets when a password is given; describe the outcome.</summary>
    private static string RestoreVault(string backup, RestoreRequest request, RestorePlan plan, CancellationToken cancellationToken)
    {
        var vaultPath = Path.Combine(backup, VaultName);
        if (!File.Exists(vaultPath))
        {
            return "no secrets vault in this backup";
        }

        if (string.IsNullOrEmpty(request.VaultPassword))
        {
            return "secrets vault present — no password given, so secrets were not restored (open secrets-vault.zip yourself)";
        }

        try
        {
            long restored = 0;
            ZipVaultSink.ExtractEach(vaultPath, request.VaultPassword, (name, stream) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Secrets are never de-duplicated, so each has a single target; buffer the small content
                // once so it can be written even though the zip stream is one-shot.
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                foreach (var target in plan.TargetsFor(name))
                {
                    if (WriteFile(() => new MemoryStream(buffer.ToArray()), target) is { Bytes: >= 0 })
                    {
                        restored++;
                    }
                }
            });
            return $"{restored:N0} secret(s) restored";
        }
        catch (Exception ex)
        {
            return $"secrets restore failed: {ex.GetType().Name}: {ex.Message}";
        }
    }

    private readonly record struct WriteOutcome(long Bytes, bool Skipped, string? Reason);

    /// <summary>Write one file to a target, skipping (never overwriting) one that already exists.</summary>
    private static WriteOutcome WriteFile(Func<Stream> openSource, string target)
    {
        try
        {
            if (File.Exists(target))
            {
                return new WriteOutcome(-1, Skipped: true, null);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = openSource();
            using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output);
            }

            return new WriteOutcome(new FileInfo(target).Length, Skipped: false, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new WriteOutcome(-1, Skipped: false, ex.GetType().Name);
        }
    }

    private static bool IsSpecialFile(string relativePath) =>
        string.Equals(relativePath, RestoreMapName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(relativePath, HashManifestName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(relativePath, VaultName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the file on disk hashes to the SHA-256 recorded at backup time (end-to-end proof).</summary>
    private static bool FileMatches(string path, string expectedSha256)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return string.Equals(Convert.ToHexString(SHA256.HashData(stream)), expectedSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Load the backup's per-file checksums (backup-relative path → SHA-256); empty if absent/broken.</summary>
    private static IReadOnlyDictionary<string, string> LoadHashManifest(string path)
    {
        if (!File.Exists(path))
        {
            return new Dictionary<string, string>();
        }

        try
        {
            var file = JsonSerializer.Deserialize<HashManifestFile>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in file?.Files ?? [])
            {
                if (!string.IsNullOrEmpty(entry.Path) && !string.IsNullOrEmpty(entry.Sha256))
                {
                    map[entry.Path.Replace('\\', '/')] = entry.Sha256;
                }
            }

            return map;
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(); // a broken manifest just means no verification
        }
    }

    /// <summary>Whether a full path is a UNC network share (\\server\share) — restore reads/writes it natively.</summary>
    private static bool IsNetworkPath(string fullPath) => fullPath.StartsWith(@"\\", StringComparison.Ordinal);

    private static string NormalizeTargetFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            throw new InvalidOperationException("Choose a folder to restore into, or restore to the original locations.");
        }

        return Path.GetFullPath(folder);
    }

    private static IReadOnlyList<RestoreMapEntry> LoadRestoreMap(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            var file = JsonSerializer.Deserialize<RestoreMapFile>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return file?.Duplicates?
                .Where(d => !string.IsNullOrEmpty(d.StoredAt))
                .Select(d => new RestoreMapEntry(d.StoredAt!, d.BelongsAt ?? []))
                .ToList() ?? [];
        }
        catch (JsonException)
        {
            return []; // a broken map just means no de-dup replication — the stored copies still restore
        }
    }

    private sealed record RestoreMapFile(int Version, IReadOnlyList<RestoreMapDto>? Duplicates);

    private sealed record RestoreMapDto(string? StoredAt, IReadOnlyList<string>? BelongsAt);

    private sealed record HashManifestFile(int Version, string? Algorithm, IReadOnlyList<HashDto>? Files);

    private sealed record HashDto(string? Path, string? Sha256);
}
