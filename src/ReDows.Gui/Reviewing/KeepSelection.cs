namespace ReDows.Gui.Reviewing;

/// <summary>
/// The set of things the user DROPPED during review (default is: keep everything, drop the junk —
/// safer, nothing is lost by forgetting to tick). Path-based: dropping a folder covers everything
/// under it; dropping a folder removes any individual child drops it now covers (no double counting).
/// Restore removes a drop. (File kept from the earlier keep-based model; it now holds the drop/trash model.)
/// </summary>
public sealed class DropSelection
{
    private readonly Dictionary<string, long> _dropped = new(StringComparer.OrdinalIgnoreCase);

    public int DroppedCount => _dropped.Count;

    public long DroppedBytes => _dropped.Values.Sum();

    /// <summary>The drop "roots" (paths with no dropped ancestor) and their sizes — what the trash lists.</summary>
    public IReadOnlyDictionary<string, long> Items => _dropped;

    public void Drop(string path, long bytes)
    {
        var normalized = Normalize(path);
        if (HasDroppedAncestor(normalized))
        {
            return; // already covered by a dropped parent folder
        }

        RemoveUnder(normalized);
        _dropped[normalized] = bytes;
    }

    public void Restore(string path) => _dropped.Remove(Normalize(path));

    /// <summary>Dropped directly, or covered by a dropped ancestor folder (hidden from the list either way).</summary>
    public bool IsDropped(string path)
    {
        var normalized = Normalize(path);
        return _dropped.ContainsKey(normalized) || HasDroppedAncestor(normalized);
    }

    private bool HasDroppedAncestor(string normalized)
    {
        foreach (var dropped in _dropped.Keys)
        {
            if (normalized.StartsWith(dropped + "\\", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private void RemoveUnder(string normalized)
    {
        var prefix = normalized + "\\";
        foreach (var dropped in _dropped.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            _dropped.Remove(dropped);
        }
    }

    private static string Normalize(string path) => path.Replace('/', '\\').TrimEnd('\\');
}
