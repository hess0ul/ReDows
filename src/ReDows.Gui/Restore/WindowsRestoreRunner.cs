using System.IO;
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
    private const string VaultName = "secrets-vault.zip";

    public Task<RestoreResultView> RunAsync(RestoreRequest request, IProgress<RestoreProgress> progress, CancellationToken cancellationToken) =>
        Task.Run(() => RunCore(request, progress, cancellationToken), cancellationToken);

    private static RestoreResultView RunCore(RestoreRequest request, IProgress<RestoreProgress> progress, CancellationToken cancellationToken)
    {
        var backup = Path.GetFullPath(request.BackupFolder);
        if (!Directory.Exists(backup))
        {
            throw new InvalidOperationException($"That backup folder does not exist: '{backup}'.");
        }

        var targetFolder = request.ToOriginalLocations ? null : NormalizeTargetFolder(request.TargetFolder);
        var plan = new RestorePlan(LoadRestoreMap(Path.Combine(backup, RestoreMapName)), request.ToOriginalLocations, targetFolder);

        long restored = 0, restoredBytes = 0, skipped = 0, seen = 0;
        var failures = new List<RestoreFailureRow>();

        foreach (var file in Directory.EnumerateFiles(backup, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(backup, file).Replace('\\', '/');
            if (IsSpecialFile(rel))
            {
                continue; // the restore map / vault are ReDows' own outputs, not user files to place
            }

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
                else
                {
                    restored++;
                    restoredBytes += outcome.Bytes;
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
            TopFailures: failures.Take(25).ToList());
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
        string.Equals(relativePath, VaultName, StringComparison.OrdinalIgnoreCase);

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
}
