namespace ReDows.Core.Rules.Globbing;

/// <summary>
/// Bounded glob over path segments. Dialect (normative, see rules/README.md):
/// '/' separates segments; a segment is a literal, a wildcard segment ('*' = zero or
/// more characters, '?' = exactly one character), or '**' (zero or more whole
/// segments). Matching is ordinal and case-insensitive. A trailing '/**' also
/// matches the base directory itself ('**' may match zero segments).
/// </summary>
public sealed class GlobPattern
{
    private enum SegmentKind
    {
        Literal,
        Wildcard,
        DoubleStar,
    }

    private readonly record struct Segment(SegmentKind Kind, string Text);

    private readonly Segment[] _segments;

    /// <summary>Specificity metric 1: more literal segments = more specific.</summary>
    public int LiteralSegmentCount { get; }

    /// <summary>Specificity metric 2 (tiebreak): fewer '**' segments = more specific.</summary>
    public int DoubleStarCount { get; }

    /// <summary>Specificity metric 3 (tiebreak): fewer wildcard characters = more specific.</summary>
    public int WildcardCharCount { get; }

    /// <summary>
    /// First segment when it is a literal (anchored patterns: the expanded root,
    /// e.g. "C:"), else null. Lets the classifier reject bindings of other
    /// volumes/profiles without running the matcher.
    /// </summary>
    public string? FirstLiteralSegment =>
        _segments.Length > 0 && _segments[0].Kind == SegmentKind.Literal ? _segments[0].Text : null;

    private GlobPattern(Segment[] segments)
    {
        _segments = segments;
        foreach (var segment in segments)
        {
            switch (segment.Kind)
            {
                case SegmentKind.Literal:
                    LiteralSegmentCount++;
                    break;
                case SegmentKind.DoubleStar:
                    DoubleStarCount++;
                    break;
                case SegmentKind.Wildcard:
                    foreach (var c in segment.Text)
                    {
                        if (c is '*' or '?')
                        {
                            WildcardCharCount++;
                        }
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// Builds a compiled glob from an expanded literal root (token expansion result,
    /// always matched literally even if it contains glob characters) and the rule's
    /// pattern segments.
    /// </summary>
    public static GlobPattern Build(IEnumerable<string> literalPrefix, IEnumerable<string> patternSegments)
    {
        var segments = new List<Segment>();
        foreach (var prefix in literalPrefix)
        {
            segments.Add(new Segment(SegmentKind.Literal, prefix));
        }

        foreach (var raw in patternSegments)
        {
            segments.Add(raw == "**"
                ? new Segment(SegmentKind.DoubleStar, raw)
                : raw.AsSpan().IndexOfAny('*', '?') >= 0
                    ? new Segment(SegmentKind.Wildcard, raw)
                    : new Segment(SegmentKind.Literal, raw));
        }

        return new GlobPattern(segments.ToArray());
    }

    public bool IsMatch(IReadOnlyList<string> pathSegments)
    {
        // Without '**' the match is a plain segment-by-segment walk: no memo
        // allocation on the hot path (the classifier runs this per item).
        if (DoubleStarCount == 0)
        {
            if (pathSegments.Count != _segments.Length)
            {
                return false;
            }

            for (var i = 0; i < _segments.Length; i++)
            {
                var segment = _segments[i];
                var matches = segment.Kind == SegmentKind.Literal
                    ? string.Equals(segment.Text, pathSegments[i], StringComparison.OrdinalIgnoreCase)
                    : MatchesName(segment.Text, pathSegments[i]);
                if (!matches)
                {
                    return false;
                }
            }

            return true;
        }

        // memo: 0 = unknown, 1 = true, 2 = false
        var memo = new byte[_segments.Length + 1, pathSegments.Count + 1];
        return Match(0, 0, pathSegments, memo);
    }

    private bool Match(int patternIndex, int pathIndex, IReadOnlyList<string> path, byte[,] memo)
    {
        if (memo[patternIndex, pathIndex] != 0)
        {
            return memo[patternIndex, pathIndex] == 1;
        }

        bool result;
        if (patternIndex == _segments.Length)
        {
            result = pathIndex == path.Count;
        }
        else
        {
            var segment = _segments[patternIndex];
            if (segment.Kind == SegmentKind.DoubleStar)
            {
                result = Match(patternIndex + 1, pathIndex, path, memo)
                    || (pathIndex < path.Count && Match(patternIndex, pathIndex + 1, path, memo));
            }
            else
            {
                result = pathIndex < path.Count
                    && (segment.Kind == SegmentKind.Literal
                        ? string.Equals(segment.Text, path[pathIndex], StringComparison.OrdinalIgnoreCase)
                        : MatchesName(segment.Text, path[pathIndex]))
                    && Match(patternIndex + 1, pathIndex + 1, path, memo);
            }
        }

        memo[patternIndex, pathIndex] = result ? (byte)1 : (byte)2;
        return result;
    }

    /// <summary>Single-segment wildcard match ('*', '?'), ordinal case-insensitive.</summary>
    public static bool MatchesName(string pattern, string name)
    {
        int p = 0, n = 0, starP = -1, starN = 0;
        while (n < name.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || CharEquals(pattern[p], name[n])))
            {
                p++;
                n++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starP = p++;
                starN = n;
            }
            else if (starP >= 0)
            {
                p = starP + 1;
                n = ++starN;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;

        static bool CharEquals(char a, char b) => char.ToUpperInvariant(a) == char.ToUpperInvariant(b);
    }
}
