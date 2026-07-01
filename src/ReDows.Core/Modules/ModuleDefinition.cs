using ReDows.Core.Scanning;

namespace ReDows.Core.Modules;

/// <summary>
/// A category module as declared in a module file (modules/*.yaml): a named,
/// data-driven detector for a whole class of items (games, media…) plus its default
/// action. Adding a category is adding a file — no engine change. The resolved,
/// per-scan form the engine consumes is <see cref="CategoryModule"/>, produced by
/// <see cref="ToCategoryModule"/> once the user's action is known.
/// </summary>
public sealed record ModuleDefinition(
    string Name,
    string Label,
    ModuleAction DefaultAction,
    IReadOnlyList<string> FolderNames,
    IReadOnlyList<string> Extensions,
    bool SaveCarveBacks)
{
    /// <summary>Report/attribution id, namespaced with ':' so it never collides with a rule or engine id.</summary>
    public string ZoneId => $"module:{Name}";

    /// <summary>Bind this definition to a chosen action, producing the engine-facing module.</summary>
    public CategoryModule ToCategoryModule(ModuleAction action) =>
        new(ZoneId, action, FolderNames, Extensions, SaveCarveBacks);
}

/// <summary>YAML/CLI string forms of <see cref="ModuleAction"/>.</summary>
public static class ModuleActions
{
    public static bool TryParse(string? text, out ModuleAction action)
    {
        action = text switch
        {
            "review" => ModuleAction.Review,
            "keep" => ModuleAction.Keep,
            "ignore" => ModuleAction.Ignore,
            _ => (ModuleAction)(-1),
        };
        return (int)action >= 0;
    }

    public static string Format(this ModuleAction action) => action switch
    {
        ModuleAction.Review => "review",
        ModuleAction.Keep => "keep",
        ModuleAction.Ignore => "ignore",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };
}
