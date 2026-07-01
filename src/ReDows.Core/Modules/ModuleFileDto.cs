namespace ReDows.Core.Modules;

/// <summary>
/// YAML shape of a module file (snake_case keys). Kept deliberately small: a module
/// is a detector plus a default action, not a fixed-verdict rule. Unknown and
/// duplicate keys are rejected by the strict loader (a typo must not silently
/// weaken detection).
/// </summary>
public sealed class ModuleFileDto
{
    public int? SchemaVersion { get; set; }

    public string? Name { get; set; }

    public string? Label { get; set; }

    public string? DefaultAction { get; set; }

    public ModuleDetectDto? Detect { get; set; }

    public bool? SaveCarveBacks { get; set; }
}

/// <summary>How a module recognises its items: by folder name, by file extension, or both.</summary>
public sealed class ModuleDetectDto
{
    public List<string>? FolderNames { get; set; }

    public List<string>? Extensions { get; set; }
}
