namespace ReDows.Core.Rules.Loading;

/// <summary>One validation problem, pointing at a file and (when known) a rule id.</summary>
public sealed record RulesetError(string File, string? RuleId, string Message)
{
    public override string ToString() =>
        RuleId is null ? $"{File}: {Message}" : $"{File}: rule '{RuleId}': {Message}";
}

/// <summary>
/// A broken ruleset is a data-loss risk, so loading is fail-closed: any error in
/// any file aborts the whole load and the engine refuses to scan. All errors are
/// reported at once.
/// </summary>
public sealed class RulesetValidationException(IReadOnlyList<RulesetError> errors)
    : Exception(BuildMessage(errors))
{
    public IReadOnlyList<RulesetError> Errors { get; } = errors;

    private static string BuildMessage(IReadOnlyList<RulesetError> errors) =>
        $"Ruleset validation failed with {errors.Count} error(s):{Environment.NewLine}" +
        string.Join(Environment.NewLine, errors.Select(e => "  - " + e));
}
