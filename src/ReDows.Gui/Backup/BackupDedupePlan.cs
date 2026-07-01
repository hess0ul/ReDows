using ReDows.Core.Backup;
using ReDows.Core.Duplicates;

namespace ReDows.Gui.Backup;

/// <summary>
/// One line of the restore map (redows-restore-map.json): a single stored copy and every original place
/// its content belongs. <see cref="StoredAt"/> is the destination-relative path the one copy was written
/// to (the most-recent "truth"); <see cref="BelongsAt"/> lists all the original source paths that held
/// the identical content — so a later restore can put it back to one, some, or all of them.
/// </summary>
public sealed record RestoreMapEntry(string StoredAt, IReadOnlyList<string> BelongsAt);

/// <summary>
/// Turns the duplicate groups found across the backup set into a de-duplication plan (pure, so it is
/// unit-tested): which copies NOT to write (every duplicate except the most-recent), the restore map to
/// record where each stored copy belongs, and how much space that saves. The content is byte-identical,
/// so storing one copy and noting the other locations loses nothing.
/// </summary>
public sealed record BackupDedupePlan(
    IReadOnlySet<string> SkipPaths,
    IReadOnlyList<RestoreMapEntry> Map,
    long DedupedItems,
    long SavedBytes)
{
    public static BackupDedupePlan Build(IReadOnlyList<DuplicateGroup> groups)
    {
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var map = new List<RestoreMapEntry>();
        long dedupedItems = 0, savedBytes = 0;

        foreach (var group in groups)
        {
            // Keep the primary (most-recent); every other copy is stored once via the primary.
            foreach (var other in group.Locations.Skip(1))
            {
                skip.Add(other.Path);
                dedupedItems++;
                savedBytes += group.Size;
            }

            map.Add(new RestoreMapEntry(
                CopyEngine.RelativePath(group.Primary.Path),
                group.Locations.Select(location => location.Path).ToList()));
        }

        return new BackupDedupePlan(skip, map, dedupedItems, savedBytes);
    }
}
