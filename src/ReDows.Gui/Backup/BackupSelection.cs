using ReDows.Core.Rules;
using ReDows.Core.Scanning;

namespace ReDows.Gui.Backup;

/// <summary>
/// Applies the review trash to the backup manifest. The rule (pure, so it is unit-tested): trashing a
/// folder removes every manifest entry under it, review items AND auto-captured config/user files,
/// because dropping a whole folder in the sorter is an explicit "I do not want any of this" that wins
/// over the rules. The one exception is a secret (capture:secret): it still goes to the encrypted vault,
/// never silently lost, even under a trashed folder (secrets-apart). A prefix-only sibling
/// ("games-backup" vs "games") is not removed. Paths compare case-insensitively on the Windows form.
/// </summary>
public static class BackupSelection
{
    private static readonly string SecretVerdict = Verdict.CaptureSecret.Format();

    /// <summary>True if the review trash removes this manifest entry from the backup.</summary>
    public static bool IsTrashed(ManifestEntry entry, IReadOnlyCollection<string> trashedPaths)
    {
        if (trashedPaths.Count == 0)
        {
            return false;
        }

        // A secret is never dropped by a folder-trash gesture: it still goes to the vault (secrets-apart).
        if (string.Equals(entry.Verdict, SecretVerdict, StringComparison.OrdinalIgnoreCase))
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
