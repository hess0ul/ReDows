using ReDows.Core.Rules;
using ReDows.Core.Scanning;

namespace ReDows.Gui.Backup;

/// <summary>
/// Applies the review trash to the backup manifest. The rule (pure, so it is unit-tested): the trash
/// removes a REVIEW item whose path is a trashed path or lives under a trashed folder. It NEVER removes
/// a CAPTURE item — config, user files and secrets are auto-kept, so dropping a folder in the sorter
/// can't lose an auto-captured file or a secret that happens to sit inside it (forget-nothing +
/// secrets-apart). Paths are compared case-insensitively on the Windows form ('\'-separated).
/// </summary>
public static class BackupSelection
{
    private static readonly string ReviewVerdict = Verdict.Review.Format();

    /// <summary>True if the review trash removes this manifest entry from the backup.</summary>
    public static bool IsTrashed(ManifestEntry entry, IReadOnlyCollection<string> trashedPaths)
    {
        if (trashedPaths.Count == 0 || !string.Equals(entry.Verdict, ReviewVerdict, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var path = Normalize(entry.Path);
        foreach (var trashed in trashedPaths)
        {
            var root = Normalize(trashed);
            if (path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string Normalize(string path) => path.Replace('/', '\\').TrimEnd('\\');
}
