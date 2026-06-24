using ReDows.Core.Rules;
using ReDows.Core.Rules.Globbing;
using ReDows.Core.Scanning;

namespace ReDows.Core.Classification;

/// <summary>The single verdict assigned to one file-system object (total accounting invariant).</summary>
public sealed record Classification(
    Verdict Verdict, string RuleId, string Stage, IReadOnlyList<RuleFlag>? Flags = null)
{
    public const string DefaultRuleId = "default.review";

    public bool HasFlag(RuleFlag flag) => Flags is not null && Flags.Contains(flag);
}

/// <summary>
/// A rule that could not be instantiated for a binding because a location token
/// had no resolved value (e.g. unread hive of another user's profile). Counted
/// and reported — never silently skipped, and never instantiated on a guessed
/// path (an ignore rule on a wrong path could silently lose data).
/// </summary>
public sealed record UninstantiatedRule(string RuleId, string Binding, string MissingToken);

/// <summary>
/// Classifies paths against a compiled ruleset. Normative semantics
/// (see rules/README.md):
/// stages in fixed order (carve_out → deny → capture), the first stage with a
/// match decides; within a stage the most specific pattern wins (more literal
/// segments, then fewer '**', then fewer wildcard characters); ties go to the
/// most conservative verdict (§0-7); a matching nested exception overrides its
/// parent (§0-9); anything unmatched falls back to REVIEW (default-to-review).
/// NOT thread-safe: one instance serves one single-threaded scan (the ancestor-
/// marker memo is an unsynchronized per-scan cache that grows with the number of
/// distinct directories probed by collision-prone rules).
/// </summary>
public sealed class Classifier
{
    private sealed record CompiledNode(
        string RuleId,
        Verdict Verdict,
        RuleCondition? When,
        GlobPattern Glob,
        IReadOnlyList<CompiledNode> Exceptions,
        IReadOnlyList<RuleFlag>? Flags);

    private static readonly RuleLayer[] StageOrder = [RuleLayer.CarveOut, RuleLayer.Deny, RuleLayer.Capture];

    private readonly List<CompiledNode>[] _byLayer;
    private readonly List<UninstantiatedRule> _uninstantiated = [];
    private readonly IFileSystemView _fileSystem;
    private readonly Dictionary<(object Condition, string Directory), bool> _markerMemo = new();

    /// <summary>Rules not instantiated for some binding (missing token value), for the completeness report.</summary>
    public IReadOnlyList<UninstantiatedRule> UninstantiatedRules => _uninstantiated;

    public Classifier(Ruleset ruleset, ScanContext context, IFileSystemView fileSystem)
    {
        _fileSystem = fileSystem;
        _byLayer = [[], [], []];

        var machineEnvironment = new Dictionary<string, string>(context.MachineEnvironment, StringComparer.OrdinalIgnoreCase);

        foreach (var rule in ruleset.Rules)
        {
            foreach (var binding in EnumerateBindings(rule.Scope, machineEnvironment, context))
            {
                var node = TryCompile(rule, binding.Resolve, out var missingToken);
                if (node is null)
                {
                    // Asymmetric policy: a rule (or any of its exceptions — they
                    // protect zones from their parent) that cannot resolve all its
                    // tokens is not instantiated for this binding, and counted.
                    _uninstantiated.Add(new UninstantiatedRule(rule.Id, binding.Label, missingToken ?? "?"));
                    continue;
                }

                _byLayer[(int)rule.Layer].Add(node);
            }
        }
    }

    public Classification Classify(string path) => Classify(ScanPaths.Split(path));

    /// <summary>Pre-split overload: the scan engine splits each path exactly once.</summary>
    public Classification Classify(string[] segments)
    {
        var conditionContext = new ConditionContext(segments, _fileSystem, _markerMemo);

        foreach (var layer in StageOrder)
        {
            var node = SelectBest(_byLayer[(int)layer], segments, conditionContext);
            if (node is null)
            {
                continue;
            }

            // Descend into matching exceptions: the innermost match wins (§0-9).
            while (true)
            {
                var exception = SelectBest(node.Exceptions, segments, conditionContext);
                if (exception is null)
                {
                    break;
                }

                node = exception;
            }

            return new Classification(node.Verdict, node.RuleId, layer.Format(), node.Flags);
        }

        return new Classification(Verdict.Review, Classification.DefaultRuleId, "default");
    }

    private CompiledNode? SelectBest(
        IReadOnlyList<CompiledNode> candidates, string[] segments, ConditionContext conditionContext)
    {
        CompiledNode? best = null;
        foreach (var candidate in candidates)
        {
            // Anchored patterns start with their expanded root: reject other
            // volumes/profiles bindings before running the matcher (hot path).
            if (candidate.Glob.FirstLiteralSegment is { } firstLiteral
                && (segments.Length == 0
                    || !string.Equals(firstLiteral, segments[0], StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (!candidate.Glob.IsMatch(segments))
            {
                continue;
            }

            if (candidate.When is not null && !candidate.When.Evaluate(conditionContext))
            {
                continue;
            }

            if (best is null || Compare(candidate, best) > 0)
            {
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>Positive when <paramref name="a"/> beats <paramref name="b"/>.</summary>
    private static int Compare(CompiledNode a, CompiledNode b)
    {
        var byLiterals = a.Glob.LiteralSegmentCount.CompareTo(b.Glob.LiteralSegmentCount);
        if (byLiterals != 0)
        {
            return byLiterals;
        }

        var byDoubleStars = b.Glob.DoubleStarCount.CompareTo(a.Glob.DoubleStarCount);
        if (byDoubleStars != 0)
        {
            return byDoubleStars;
        }

        var byWildcards = b.Glob.WildcardCharCount.CompareTo(a.Glob.WildcardCharCount);
        if (byWildcards != 0)
        {
            return byWildcards;
        }

        var byConservativeness = a.Verdict.ConservativenessRank().CompareTo(b.Verdict.ConservativenessRank());
        if (byConservativeness != 0)
        {
            return byConservativeness;
        }

        // Full tie: deterministic order by id so results never depend on load order.
        return string.CompareOrdinal(b.RuleId, a.RuleId);
    }

    private static CompiledNode? TryCompile(Rule rule, Func<LocationToken?, string?> resolveRoot, out string? missingToken)
    {
        var glob = TryCompilePattern(rule.Pattern, resolveRoot, out missingToken);
        if (glob is null)
        {
            return null;
        }

        var exceptions = new List<CompiledNode>();
        foreach (var exception in rule.Exceptions)
        {
            var compiled = TryCompile(exception, resolveRoot, out missingToken);
            if (compiled is null)
            {
                return null;
            }

            exceptions.Add(compiled);
        }

        return new CompiledNode(rule.Id, rule.Verdict, rule.When, glob, exceptions, rule.Flags);
    }

    private static CompiledNode? TryCompile(RuleException exception, Func<LocationToken?, string?> resolveRoot, out string? missingToken)
    {
        var glob = TryCompilePattern(exception.Pattern, resolveRoot, out missingToken);
        if (glob is null)
        {
            return null;
        }

        var nested = new List<CompiledNode>();
        foreach (var child in exception.Exceptions)
        {
            var compiled = TryCompile(child, resolveRoot, out missingToken);
            if (compiled is null)
            {
                return null;
            }

            nested.Add(compiled);
        }

        return new CompiledNode(exception.Id, exception.Verdict, exception.When, glob, nested, exception.Flags);
    }

    private static GlobPattern? TryCompilePattern(
        PathPattern pattern, Func<LocationToken?, string?> resolveRoot, out string? missingToken)
    {
        var root = resolveRoot(pattern.Token);
        if (root is null)
        {
            missingToken = pattern.Token?.Name ?? "?";
            return null;
        }

        missingToken = null;
        return GlobPattern.Build(ScanPaths.Split(root), pattern.TailSegments);
    }

    /// <summary>
    /// One binding per rule instantiation: machine rules bind once, user rules bind
    /// per profile (ProfileList — never a Users\* glob), volume/drive rules bind per
    /// volume. Exceptions are compiled with the same binding as their parent.
    /// A resolver returns null when the binding has no value for the token.
    /// </summary>
    private static IEnumerable<(string Label, Func<LocationToken?, string?> Resolve)> EnumerateBindings(
        RuleScope scope, IReadOnlyDictionary<string, string> machineEnvironment, ScanContext context)
    {
        switch (scope)
        {
            case RuleScope.Machine:
                yield return ("machine", token => token switch
                {
                    { Kind: LocationTokenKind.MachineEnvironment } =>
                        machineEnvironment.TryGetValue(token.Name, out var value) ? value : null,
                    _ => throw new InvalidOperationException("machine patterns must start with a machine environment token"),
                });
                break;

            case RuleScope.User:
                foreach (var profile in context.Profiles)
                {
                    var environment = new Dictionary<string, string>(profile.Environment, StringComparer.OrdinalIgnoreCase);
                    var knownFolders = new Dictionary<string, string>(profile.KnownFolders, StringComparer.OrdinalIgnoreCase);
                    yield return ($"profile:{profile.UserName}", token => token switch
                    {
                        { Kind: LocationTokenKind.UserProfileRoot } => profile.RootPath,
                        { Kind: LocationTokenKind.ProfileEnvironment } =>
                            environment.TryGetValue(token.Name, out var value) ? value : null,
                        { Kind: LocationTokenKind.KnownFolder } =>
                            knownFolders.TryGetValue(token.Name, out var value) ? value : null,
                        _ => throw new InvalidOperationException("user patterns must start with a per-profile token"),
                    });
                }

                break;

            case RuleScope.Volume:
            case RuleScope.Drive:
                foreach (var volume in context.Volumes)
                {
                    yield return ($"volume:{volume.RootPath}", _ => volume.RootPath);
                }

                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(scope));
        }
    }
}
