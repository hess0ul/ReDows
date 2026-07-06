using System.Text.Json;

namespace ReDows.Core.Backup;

/// <summary>
/// The per-file checksum manifest (redows-hashes.json) written next to a backup and read back by a
/// restore to prove every restored file is byte-identical to its original. Both front-ends write it and
/// the restore reads it through THIS one type, so the on-disk format can never drift between them.
/// </summary>
public static class BackupHashManifest
{
    /// <summary>The manifest's fixed file name, at the backup root.</summary>
    public const string FileName = "redows-hashes.json";

    private const int CurrentVersion = 1;
    private const string HashAlgorithm = "SHA-256";

    /// <summary>Write the SHA-256 manifest at the backup root.</summary>
    public static void Write(string destinationDirectory, IReadOnlyList<FileHash> hashes)
    {
        var json = JsonSerializer.Serialize(
            new { version = CurrentVersion, algorithm = HashAlgorithm, files = hashes.Select(h => new { path = h.RelativePath, sha256 = h.Sha256 }) },
            new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        File.WriteAllText(Path.Combine(destinationDirectory, FileName), json);
    }

    /// <summary>
    /// Read the manifest as a backup-relative-path → SHA-256 map (forward slashes). Returns empty if the
    /// file is absent or unreadable. A missing or broken manifest just means "no verification", never a crash.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Read(string path)
    {
        if (!File.Exists(path))
        {
            return new Dictionary<string, string>();
        }

        try
        {
            var file = JsonSerializer.Deserialize<ManifestFile>(
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

    private sealed record ManifestFile(int Version, string? Algorithm, IReadOnlyList<HashDto>? Files);

    private sealed record HashDto(string? Path, string? Sha256);
}
