namespace ReDows.Core.Triage;

/// <summary>
/// The fast-path decision for one entry. <see cref="Importance"/> uses the Review colour keys —
/// "keep" (blue), "maybe" (pink), "drop" (purple) — or "" when no rule matched (the entry is UNKNOWN and
/// should be handed to the AI). <see cref="IsSecret"/> marks a key/credential (kept, flagged, never
/// exposed). <see cref="IsCloudSync"/> marks a file in a cloud-synced folder (dropped, but the user is
/// reminded to sync first). <see cref="Reason"/> is the human "why", shown as the row's tooltip.
/// </summary>
public sealed record TriageVerdict(string Importance, bool IsSecret, bool IsCloudSync, string Reason)
{
    /// <summary>No rule matched — the entry is unknown and should be sent to the AI.</summary>
    public static readonly TriageVerdict Unknown = new("", false, false, "");

    public bool IsKnown => Importance.Length > 0;
}

/// <summary>
/// The data-driven "fast path": classify an entry from its METADATA ALONE (name, extension, path
/// segments, size) so obvious files never cost an AI call. Pure — no I/O, never opens a file (the same
/// privacy guarantee as <c>AiPayload</c>). Rules come from <c>triage/file-triage.yaml</c> (generic +
/// bilingual). Priority is forget-nothing: <b>secret &gt; keep &gt; review &gt; drop</b>, and anything
/// unmatched stays <see cref="TriageVerdict.Unknown"/> (→ the AI decides). A tiny image is downgraded to
/// "maybe" (likely an icon/thumbnail), never dropped on size alone.
/// </summary>
public sealed class FileTriage
{
    private readonly HashSet<string> _keepExt;
    private readonly HashSet<string> _reviewExt;
    private readonly HashSet<string> _dropExt;
    private readonly HashSet<string> _secretExt;
    private readonly HashSet<string> _imageExt;
    private readonly HashSet<string> _keepFolders;
    private readonly HashSet<string> _dropFolders;
    private readonly HashSet<string> _cloudFolders;
    private readonly IReadOnlyList<string> _secretNames;
    private readonly IReadOnlyList<string> _keepNames;
    private readonly long _thumbnailMaxBytes;

    public FileTriage(
        IEnumerable<string> keepExtensions,
        IEnumerable<string> reviewExtensions,
        IEnumerable<string> dropExtensions,
        IEnumerable<string> secretExtensions,
        IEnumerable<string> imageExtensions,
        IEnumerable<string> secretNames,
        IEnumerable<string> keepNames,
        IEnumerable<string> keepFolders,
        IEnumerable<string> dropFolders,
        IEnumerable<string> cloudFolders,
        long thumbnailMaxBytes)
    {
        _keepExt = Extensions(keepExtensions);
        _reviewExt = Extensions(reviewExtensions);
        _dropExt = Extensions(dropExtensions);
        _secretExt = Extensions(secretExtensions);
        _imageExt = Extensions(imageExtensions);
        _keepFolders = Segments(keepFolders);
        _dropFolders = Segments(dropFolders);
        _cloudFolders = Segments(cloudFolders);
        _secretNames = Patterns(secretNames);
        _keepNames = Patterns(keepNames);
        _thumbnailMaxBytes = thumbnailMaxBytes > 0 ? thumbnailMaxBytes : 0;
    }

    /// <summary>
    /// Decide one entry from its metadata. Files AND folder entries go through the same path-segment
    /// checks (an entry's own name is the last segment), so a "node_modules" folder or a file inside one
    /// both resolve to drop. Returns <see cref="TriageVerdict.Unknown"/> when nothing matches.
    /// </summary>
    public TriageVerdict Classify(string name, bool isDirectory, long bytes, string fullPath)
    {
        var lowerName = name.Trim().ToLowerInvariant();
        var ext = isDirectory ? "" : Extension(lowerName);
        var segments = fullPath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);

        // 1. SECRET wins over everything — a key/credential is never dropped and never exposed.
        if (MatchesAny(lowerName, _secretNames) || (ext.Length > 0 && _secretExt.Contains(ext)))
        {
            return new TriageVerdict("keep", IsSecret: true, false, "Looks like a key or credential — kept and flagged secret.");
        }

        // 2. Inside a protected folder (game saves, backups…) → keep, even if the extension says otherwise.
        if (segments.Any(_keepFolders.Contains))
        {
            return new TriageVerdict("keep", false, false, "Inside a keep folder (game saves / backup).");
        }

        // 3. Name looks like a personal document (CV, ID, invoice…) → keep.
        if (MatchesAny(lowerName, _keepNames))
        {
            return new TriageVerdict("keep", false, false, "Name suggests a personal document.");
        }

        // 4. In a cloud-synced folder → drop, but remind the user to sync it first so nothing is lost.
        if (segments.Any(_cloudFolders.Contains))
        {
            return new TriageVerdict("drop", false, IsCloudSync: true, "In a cloud-synced folder — make sure it's synced, then it's safe to drop.");
        }

        // 5. Inside a regenerable folder (node_modules, caches…) → drop; this beats a keep-extension asset
        //    (a .png inside node_modules is a package asset, not a memory).
        if (segments.Any(_dropFolders.Contains))
        {
            return new TriageVerdict("drop", false, false, "Inside a regenerable folder (dependencies / cache).");
        }

        if (ext.Length > 0)
        {
            // 6. A memory or personal document → keep, unless it's a TINY image (icon/thumbnail).
            if (_keepExt.Contains(ext))
            {
                if (_imageExt.Contains(ext) && _thumbnailMaxBytes > 0 && bytes > 0 && bytes < _thumbnailMaxBytes)
                {
                    return new TriageVerdict("maybe", false, false, "Tiny image — likely an icon or thumbnail, not a memory.");
                }

                return new TriageVerdict("keep", false, false, "Worth keeping (memory or user data).");
            }

            // 7. Could hold anything (VM/disk image, archive, ebook) → review.
            if (_reviewExt.Contains(ext))
            {
                return new TriageVerdict("maybe", false, false, "Could hold anything — worth a look.");
            }

            // 8. Cache / temp / log → drop.
            if (_dropExt.Contains(ext))
            {
                return new TriageVerdict("drop", false, false, "Cache, temp or log — it gets regenerated.");
            }
        }

        // 9. Nothing matched → let the AI judge it.
        return TriageVerdict.Unknown;
    }

    private static HashSet<string> Extensions(IEnumerable<string> values) =>
        new(values.Select(v => v.Trim().TrimStart('.').ToLowerInvariant()).Where(v => v.Length > 0), StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> Segments(IEnumerable<string> values) =>
        new(values.Select(v => v.Trim()).Where(v => v.Length > 0), StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<string> Patterns(IEnumerable<string> values) =>
        values.Select(v => v.Trim().ToLowerInvariant()).Where(v => v.Length > 0).ToList();

    private static string Extension(string lowerName)
    {
        var dot = lowerName.LastIndexOf('.');
        // A leading-dot name (".env", ".npmrc") has no real extension — it is matched by the secret NAME
        // rules instead. A genuine extension has text on both sides of the last dot.
        return dot > 0 && dot < lowerName.Length - 1 ? lowerName[(dot + 1)..] : "";
    }

    private static bool MatchesAny(string text, IReadOnlyList<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            if (Wildcard(text, pattern))
            {
                return true;
            }
        }

        return false;
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
