namespace ReDows.Core.Duplicates;

/// <summary>A file to consider for de-duplication: its path and byte length.</summary>
public sealed record FileRef(string Path, long Size);

/// <summary>
/// A set of byte-identical files. Keeping ONE copy would free the rest, so
/// <see cref="ReclaimableBytes"/> is the size times the number of extra copies.
/// </summary>
public sealed record DuplicateGroup(string ContentHash, long Size, IReadOnlyList<string> Paths)
{
    public int Count => Paths.Count;

    public long ReclaimableBytes => Size * (Paths.Count - 1);
}

/// <summary>
/// Hashes file content. A seam: the real implementation reads bytes off disk; a test fakes it.
/// Returns null when a file cannot be read (locked, denied) — the finder then skips it, so an
/// unreadable file is simply not de-duplicated, never wrongly grouped. <see cref="PartialHash"/>
/// covers a cheap prefix so files that differ early are separated without a full read;
/// <see cref="FullHash"/> confirms byte-for-byte identity.
/// </summary>
public interface IFileHasher
{
    string? PartialHash(string path);

    string? FullHash(string path);
}

/// <summary>
/// Finds byte-identical files. Read-only and certainty-first: identity is always confirmed by a
/// full content hash, so two files are only ever reported as duplicates when they match
/// byte-for-byte (never "they look alike"). It PROPOSES — it never deletes anything.
/// </summary>
public static class DuplicateFinder
{
    /// <summary>
    /// Three passes, cheapest first: same size → same prefix hash → same full hash. Most files are
    /// alone at their exact size and are dropped without being read at all; of the rest, a cheap
    /// prefix hash separates the ones that differ early; only the survivors are fully hashed.
    /// Empty files and files below <paramref name="minSize"/> are ignored. <paramref name="onFullHash"/>
    /// fires once per full hash (progress). Groups are returned most-reclaimable first.
    /// </summary>
    public static IReadOnlyList<DuplicateGroup> Find(
        IEnumerable<FileRef> files, IFileHasher hasher, long minSize = 1, Action<long>? onFullHash = null)
    {
        var bySize = new Dictionary<long, List<string>>();
        foreach (var file in files)
        {
            if (file.Size <= 0 || file.Size < minSize)
            {
                continue;
            }

            GetList(bySize, file.Size).Add(file.Path);
        }

        var groups = new List<DuplicateGroup>();
        foreach (var (size, sameSize) in bySize)
        {
            if (sameSize.Count < 2)
            {
                continue; // unique size can't have a duplicate — never read
            }

            // Pass 2: a cheap prefix hash so files differing early are never fully read.
            var byPrefix = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var path in sameSize)
            {
                var prefix = hasher.PartialHash(path);
                if (prefix is not null)
                {
                    GetList(byPrefix, prefix).Add(path);
                }
            }

            foreach (var samePrefix in byPrefix.Values)
            {
                if (samePrefix.Count < 2)
                {
                    continue;
                }

                // Pass 3: confirm byte-for-byte identity with a full content hash.
                var byFull = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                foreach (var path in samePrefix)
                {
                    onFullHash?.Invoke(size);
                    var full = hasher.FullHash(path);
                    if (full is not null)
                    {
                        GetList(byFull, full).Add(path);
                    }
                }

                foreach (var (hash, identical) in byFull)
                {
                    if (identical.Count >= 2)
                    {
                        groups.Add(new DuplicateGroup(hash, size, identical));
                    }
                }
            }
        }

        groups.Sort((a, b) => b.ReclaimableBytes.CompareTo(a.ReclaimableBytes));
        return groups;
    }

    private static List<string> GetList<TKey>(Dictionary<TKey, List<string>> map, TKey key)
        where TKey : notnull
    {
        if (!map.TryGetValue(key, out var list))
        {
            list = [];
            map[key] = list;
        }

        return list;
    }
}
