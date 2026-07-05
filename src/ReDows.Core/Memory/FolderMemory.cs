using ReDows.Core.Rules;

namespace ReDows.Core.Memory;

/// <summary>
/// One thing ReDows RECOGNISES: a folder or app matched by name, with an OPTIONAL importance override
/// (keep/maybe/drop — used when the memory is more confident than the scan verdict) and a human
/// <see cref="Note"/> (2-3 sentences) explaining what it is and what matters inside.
/// <para><see cref="Scope"/> decides how far the entry reaches: "subtree" (default) tints the folder AND
/// everything under it (right for node_modules, a cache, game saves…); "self" tints ONLY the folder
/// itself, so its children are judged on their own (right for a mixed CONTAINER like Documents or AppData,
/// which holds both your files and app-made subfolders — the container must not stamp "personal" onto a
/// game's data folder that lives inside it).</para>
/// </summary>
public sealed record KnownEntry(string Match, string? Importance, string Note, string Scope = "subtree");

/// <summary>Maps a scan verdict to a Review colour key, so a scanned tree can be tinted for free.</summary>
public static class ScanMemory
{
    /// <summary>keep = blue (user data / config), maybe = pink (review), drop = purple (ignored / not backed up).</summary>
    public static string ImportanceOf(Verdict verdict) => verdict switch
    {
        Verdict.CaptureConfig or Verdict.CaptureUser or Verdict.CaptureSecret => "keep",
        Verdict.Review => "maybe",
        _ => "drop", // Ignore, NoteOnly — not part of the backup
    };
}

/// <summary>
/// ReDows' MEMORY of well-known folders and apps. Given an entry's name and path, it returns a rich
/// note — and, when confident, a colour — for the DEEPEST thing it recognises (so <c>…\AppData\Local\
/// Discord</c> is described as "Discord", not the generic "AppData"). It knows what an AppData or a
/// node_modules folder is EVERYWHERE, without opening anything: pure, data-driven, locale-safe (it
/// matches folder-NAME segments, never absolute personal paths). Rules come from memory/known.yaml.
/// </summary>
public sealed class FolderMemory
{
    private readonly IReadOnlyList<(KnownEntry Entry, string MatchLower)> _entries;

    public FolderMemory(IEnumerable<KnownEntry> entries) =>
        _entries = entries.Select(e => (e, e.Match.Trim().ToLowerInvariant())).ToList();

    /// <summary>How many known entries the memory holds (0 = no memory loaded).</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// The most SPECIFIC known entry for this path (matching the deepest path segment first), or null
    /// when ReDows recognises nothing about it.
    /// </summary>
    public KnownEntry? Describe(string name, string fullPath)
    {
        var segments = fullPath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        for (var i = segments.Length - 1; i >= 0; i--) // deepest segment first = most specific
        {
            var isOwnSegment = i == segments.Length - 1; // the entry being described (not an ancestor folder)
            var segment = segments[i].ToLowerInvariant();
            foreach (var (entry, matchLower) in _entries)
            {
                // A "self"-scoped container (Documents, AppData…) describes only ITSELF — it must not tint a
                // child that lives inside it (e.g. a game's data folder under Documents). Only a "subtree"
                // entry reaches down into its descendants.
                if (!isOwnSegment && !entry.Scope.Equals("subtree", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (Wildcard(segment, matchLower))
                {
                    return entry;
                }
            }
        }

        return null;
    }

    /// <summary>Case-insensitive glob ('*' = any run, '?' = one char); inputs are already lowercased.</summary>
    private static bool Wildcard(string text, string pattern)
    {
        int ti = 0, pi = 0, star = -1, mark = 0;
        while (ti < text.Length)
        {
            if (pi < pattern.Length && (pattern[pi] == '?' || pattern[pi] == text[ti]))
            {
                ti++;
                pi++;
            }
            else if (pi < pattern.Length && pattern[pi] == '*')
            {
                star = pi++;
                mark = ti;
            }
            else if (star >= 0)
            {
                pi = star + 1;
                ti = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (pi < pattern.Length && pattern[pi] == '*')
        {
            pi++;
        }

        return pi == pattern.Length;
    }
}
