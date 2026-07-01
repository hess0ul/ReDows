namespace ReDows.Core.Modules;

/// <summary>
/// A malformed module file is a corruption of a detector the user relies on, so the
/// present-but-broken case is fail-closed: any error in any file aborts the load and
/// all errors are reported at once. (A MISSING module directory is not an error — see
/// <see cref="ModuleLoader.LoadDirectory"/> — it simply means "no modules".)
/// </summary>
public sealed class ModuleValidationException(IReadOnlyList<string> errors)
    : Exception(BuildMessage(errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;

    private static string BuildMessage(IReadOnlyList<string> errors) =>
        $"Module validation failed with {errors.Count} error(s):{Environment.NewLine}" +
        string.Join(Environment.NewLine, errors.Select(e => "  - " + e));
}
