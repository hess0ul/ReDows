namespace ReDows.Core.Rules;

/// <summary>
/// A rule's match pattern: a bounded glob whose first segment may be a location
/// token, per the locale-independence invariant. Hardcoded absolute paths are
/// rejected. Scope determines which tokens are legal and how the pattern is
/// anchored (see rules/README.md for the normative grammar).
/// </summary>
public sealed class PathPattern
{
    public string Raw { get; }

    /// <summary>The leading location token, or null for volume/drive patterns.</summary>
    public LocationToken? Token { get; }

    /// <summary>Pattern segments after the token (all segments when no token).</summary>
    public IReadOnlyList<string> TailSegments { get; }

    private PathPattern(string raw, LocationToken? token, IReadOnlyList<string> tailSegments)
    {
        Raw = raw;
        Token = token;
        TailSegments = tailSegments;
    }

    /// <summary>Parses and validates a pattern for the given scope. Returns null with an error message on failure.</summary>
    public static PathPattern? Parse(string? raw, RuleScope scope, out string? error)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "match is required and must not be empty";
            return null;
        }

        if (raw.Contains('\\'))
        {
            error = "match must use '/' as the segment separator (never '\\')";
            return null;
        }

        var segments = raw.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            error = "match contains no path segments";
            return null;
        }

        if (segments.Any(s => s.Contains(':')))
        {
            error = "match must not contain an absolute path (drive letters are forbidden; anchor with a location token or a scope)";
            return null;
        }

        for (var i = 1; i < segments.Length; i++)
        {
            if (segments[i].StartsWith('%') || segments[i].StartsWith('<')
                || segments[i].StartsWith("FOLDERID_", StringComparison.OrdinalIgnoreCase))
            {
                error = $"location tokens are only allowed as the first segment (found '{segments[i]}')";
                return null;
            }
        }

        var first = segments[0];
        LocationToken? token = null;

        if (first.Length > 2 && first.StartsWith('%') && first.EndsWith('%'))
        {
            var name = first[1..^1];
            switch (scope)
            {
                case RuleScope.Machine when LocationTokens.TryResolveMachineEnvironment(name, out var canonical):
                    token = new LocationToken(LocationTokenKind.MachineEnvironment, canonical);
                    break;
                case RuleScope.Machine:
                    error = $"'%{name}%' is not a known machine environment token";
                    return null;
                case RuleScope.User when LocationTokens.TryResolveProfileEnvironment(name, out var canonical):
                    token = new LocationToken(LocationTokenKind.ProfileEnvironment, canonical);
                    break;
                case RuleScope.User:
                    error = $"'%{name}%' is not a known per-profile environment token";
                    return null;
                default:
                    error = $"scope '{scope}' patterns must not use environment tokens";
                    return null;
            }
        }
        else if (string.Equals(first, "<UserProfile>", StringComparison.OrdinalIgnoreCase))
        {
            if (scope != RuleScope.User)
            {
                error = "<UserProfile> is only valid in user-scoped patterns";
                return null;
            }

            token = new LocationToken(LocationTokenKind.UserProfileRoot, "UserProfile");
        }
        else if (first.StartsWith("FOLDERID_", StringComparison.OrdinalIgnoreCase))
        {
            if (scope != RuleScope.User)
            {
                error = "Known Folder tokens are only valid in user-scoped patterns";
                return null;
            }

            var name = first["FOLDERID_".Length..];
            if (!LocationTokens.TryResolveKnownFolder(name, out var canonical))
            {
                error = $"'{first}' is not a known FOLDERID token";
                return null;
            }

            token = new LocationToken(LocationTokenKind.KnownFolder, canonical);
        }
        else
        {
            switch (scope)
            {
                case RuleScope.Machine or RuleScope.User:
                    error = $"scope '{scope}' patterns must start with a location token (got '{first}')";
                    return null;
                case RuleScope.Volume when first == "**":
                    error = "volume patterns are anchored at the volume root and must not start with '**' (use scope 'drive' for floating patterns)";
                    return null;
                case RuleScope.Drive when first != "**":
                    error = "drive patterns are floating and must start with '**/'";
                    return null;
            }
        }

        var tail = token is null ? segments : segments[1..];
        error = null;
        return new PathPattern(raw, token, tail);
    }
}
