namespace ReDows.Core.Rules;

/// <summary>A loaded, validated ruleset (all files merged; load order is irrelevant).</summary>
public sealed record Ruleset(int SchemaVersion, IReadOnlyList<Rule> Rules);

/// <summary>
/// A validated classification rule. Exceptions are nested rules with their own
/// verdict, evaluated before their parent (deny-list §0-9): an ignore rule with
/// exceptions is the IGNORE_EXC construct of the design notes.
/// </summary>
public sealed record Rule(
    string Id,
    RuleLayer Layer,
    RuleScope Scope,
    PathPattern Pattern,
    Verdict Verdict,
    RulePriority Priority,
    RuleCondition? When,
    BareNameClass? BareNameClass,
    IReadOnlyList<RuleException> Exceptions,
    string? Source,
    string? Note,
    IReadOnlyList<RuleFlag>? Flags = null)
{
    public IReadOnlyList<RuleFlag> FlagList => Flags ?? [];
}

/// <summary>A nested exception to a rule. Inherits the parent's scope and anchoring.</summary>
public sealed record RuleException(
    string Id,
    PathPattern Pattern,
    Verdict Verdict,
    RulePriority Priority,
    RuleCondition? When,
    IReadOnlyList<RuleException> Exceptions,
    string? Source,
    string? Note,
    IReadOnlyList<RuleFlag>? Flags = null)
{
    public IReadOnlyList<RuleFlag> FlagList => Flags ?? [];
}
