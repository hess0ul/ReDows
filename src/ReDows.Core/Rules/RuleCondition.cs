using ReDows.Core.Rules.Globbing;
using ReDows.Core.Scanning;

namespace ReDows.Core.Rules;

/// <summary>
/// Composable context conditions (deny-list §0-8). The composition frame (all/any)
/// is part of the V1 core so that new predicates can be added later without
/// re-specifying existing rules. V1 ships one predicate: ancestor_marker.
/// </summary>
public abstract record RuleCondition
{
    public abstract bool Evaluate(in ConditionContext context);
}

/// <summary>
/// Evaluation context for conditions. <paramref name="MarkerMemo"/> is an optional
/// per-scan cache keyed by (condition, directory): without it, every item inside a
/// matched zone would re-enumerate all its ancestors (quadratic on deep trees).
/// </summary>
public readonly record struct ConditionContext(
    IReadOnlyList<string> ItemSegments,
    IFileSystemView FileSystem,
    IDictionary<(object Condition, string Directory), bool>? MarkerMemo = null);

public sealed record AllOfCondition(IReadOnlyList<RuleCondition> Conditions) : RuleCondition
{
    public override bool Evaluate(in ConditionContext context)
    {
        foreach (var condition in Conditions)
        {
            if (!condition.Evaluate(context))
            {
                return false;
            }
        }

        return true;
    }
}

public sealed record AnyOfCondition(IReadOnlyList<RuleCondition> Conditions) : RuleCondition
{
    public override bool Evaluate(in ConditionContext context)
    {
        foreach (var condition in Conditions)
        {
            if (condition.Evaluate(context))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// True when NONE of the child conditions is true (logical NOT-any). Completes the
/// all/any/none algebra so a rule can require the absence of a marker — e.g. a
/// standalone executable is one whose tree has an *.exe but NO uninstaller.
/// </summary>
public sealed record NoneOfCondition(IReadOnlyList<RuleCondition> Conditions) : RuleCondition
{
    public override bool Evaluate(in ConditionContext context)
    {
        foreach (var condition in Conditions)
        {
            if (condition.Evaluate(context))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// True when any ancestor directory of the matched item (up to and including the
/// volume root) contains an entry whose name matches one of the marker globs.
/// This is how a collision-prone bare-name ignore proves it sits inside a real
/// project tree (e.g. 'build' next to package.json / *.sln / .git).
/// </summary>
public sealed record AncestorMarkerCondition(IReadOnlyList<string> Markers) : RuleCondition
{
    public override bool Evaluate(in ConditionContext context)
    {
        for (var depth = context.ItemSegments.Count - 1; depth >= 1; depth--)
        {
            var directory = string.Join('/', context.ItemSegments.Take(depth));
            if (DirectoryHasMarker(context, directory))
            {
                return true;
            }
        }

        return false;
    }

    private bool DirectoryHasMarker(in ConditionContext context, string directory)
    {
        var memo = context.MarkerMemo;
        if (memo is not null && memo.TryGetValue((this, directory), out var cached))
        {
            return cached;
        }

        var found = false;
        foreach (var name in context.FileSystem.EnumerateEntryNames(directory))
        {
            foreach (var marker in Markers)
            {
                if (GlobPattern.MatchesName(marker, name))
                {
                    found = true;
                    break;
                }
            }

            if (found)
            {
                break;
            }
        }

        if (memo is not null)
        {
            memo[(this, directory)] = found;
        }

        return found;
    }
}
