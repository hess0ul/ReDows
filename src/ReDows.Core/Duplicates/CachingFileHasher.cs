using System.Text.Json;

namespace ReDows.Core.Duplicates;

/// <summary>One remembered full-content hash: the file, and its size and last-write time (ticks) at the
/// moment it was hashed, so a reader can trust the hash only while the file is unchanged.</summary>
public sealed record HashCacheEntry(string Path, long Size, long ModifiedTicks, string Hash);

/// <summary>
/// An <see cref="IFileHasher"/> that remembers full-content hashes so a later pass does not recompute them.
/// It wraps a real hasher and a cache keyed by path; a cached hash is reused ONLY when the file's current
/// size and last-write time still match what was recorded, so an edited file is re-hashed, never wrongly
/// matched. The scan's duplicate hunt fills the cache; the backup's de-duplication seeds itself from it, so
/// a file hashed once during the scan is not read again during the backup. Prefix hashes are cheap and
/// always delegate to the inner hasher.
/// </summary>
public sealed class CachingFileHasher : IFileHasher
{
    private readonly IFileHasher _inner;
    private readonly Func<string, long> _sizeOf;
    private readonly Func<string, DateTime> _modifiedUtc;
    private readonly Dictionary<string, HashCacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    public CachingFileHasher(
        IFileHasher inner,
        Func<string, long> sizeOf,
        Func<string, DateTime> modifiedUtc,
        IEnumerable<HashCacheEntry>? seed = null)
    {
        _inner = inner;
        _sizeOf = sizeOf;
        _modifiedUtc = modifiedUtc;
        if (seed is not null)
        {
            foreach (var entry in seed)
            {
                _cache[entry.Path] = entry;
            }
        }
    }

    public string? PartialHash(string path) => _inner.PartialHash(path);

    public string? FullHash(string path)
    {
        var size = _sizeOf(path);
        var ticks = _modifiedUtc(path).Ticks;
        if (_cache.TryGetValue(path, out var cached) && cached.Size == size && cached.ModifiedTicks == ticks)
        {
            return cached.Hash; // unchanged since it was hashed: reuse
        }

        var hash = _inner.FullHash(path);
        if (hash is not null && size >= 0)
        {
            _cache[path] = new HashCacheEntry(path, size, ticks, hash);
        }

        return hash;
    }

    /// <summary>The hashes computed or reused so far: what the scan persists for the backup to reuse.</summary>
    public IReadOnlyCollection<HashCacheEntry> Entries => _cache.Values;
}

/// <summary>
/// Reads and writes the hash cache the scan leaves for the backup (a small JSON list). Best-effort: a
/// missing or corrupt file yields an empty cache, so the backup simply re-hashes, never a crash.
/// </summary>
public static class HashCache
{
    /// <summary>The cache file the scan writes next to its manifest.</summary>
    public const string FileName = "last-scan-hashes.json";

    public static void Write(string path, IReadOnlyCollection<HashCacheEntry> entries)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(entries));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Reusing hashes is only an optimization; a failed write must never break the scan.
        }
    }

    public static IReadOnlyList<HashCacheEntry> Read(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<List<HashCacheEntry>>(File.ReadAllText(path)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }
}
