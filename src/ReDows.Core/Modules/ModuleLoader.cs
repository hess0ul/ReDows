using ReDows.Core.Scanning;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ReDows.Core.Modules;

/// <summary>
/// Loads category modules from a directory of module files (modules/*.yaml).
/// <para>
/// Fail-SAFE on absence: a missing directory or an empty one yields no modules, and
/// the scan then behaves exactly as before (everything the ruleset would review
/// stays review — nothing lost). Unlike the ruleset, modules are optional and their
/// default is a no-op, so their absence is never an error.
/// </para>
/// <para>
/// Fail-CLOSED on corruption: a present but malformed file (bad YAML, unknown key,
/// invalid action, missing detector, duplicate name) aborts the whole load. A
/// detector the user relies on must work or say why it cannot — never be silently
/// skipped.
/// </para>
/// Modules live OUTSIDE rules/ on purpose: a module is a detector plus a
/// user-selectable action, not a fixed-verdict rule, so it must never be swept into
/// the ruleset (which would break the fail-closed rule loader and the rule count).
/// </summary>
public static class ModuleLoader
{
    public const int SupportedSchemaVersion = 1;

    public static IReadOnlyList<ModuleDefinition> LoadDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var paths = Directory.EnumerateFiles(directory, "*.yaml", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(directory, "*.yml", SearchOption.AllDirectories))
            // Win32 3-char-extension quirk: "*.yml" also matches ".ymlx".
            .Where(f => f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
                || f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var errors = new List<string>();
        var files = new List<(string Path, string Content)>();
        foreach (var path in paths)
        {
            var relative = Path.GetRelativePath(directory, path);
            try
            {
                files.Add((relative, File.ReadAllText(path)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{relative}: file unreadable ({ex.GetType().Name}: {ex.Message})");
            }
        }

        if (errors.Count > 0)
        {
            throw new ModuleValidationException(errors);
        }

        return LoadFiles(files);
    }

    public static IReadOnlyList<ModuleDefinition> LoadFiles(IEnumerable<(string Path, string Content)> files)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .WithDuplicateKeyChecking()
            .Build();

        var errors = new List<string>();
        var modules = new List<ModuleDefinition>();
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (path, content) in files)
        {
            ModuleFileDto? dto;
            try
            {
                dto = deserializer.Deserialize<ModuleFileDto>(content);
            }
            catch (YamlException ex)
            {
                errors.Add($"{path}: YAML error: {ex.Message}");
                continue;
            }

            if (dto is null)
            {
                errors.Add($"{path}: file is empty");
                continue;
            }

            var module = Build(path, dto, seen, errors);
            if (module is not null)
            {
                modules.Add(module);
            }
        }

        if (errors.Count > 0)
        {
            throw new ModuleValidationException(errors);
        }

        return modules;
    }

    private static ModuleDefinition? Build(
        string file, ModuleFileDto dto, Dictionary<string, string> seen, List<string> errors)
    {
        var before = errors.Count;

        if (dto.SchemaVersion != SupportedSchemaVersion)
        {
            errors.Add($"{file}: schema_version must be {SupportedSchemaVersion} (got {dto.SchemaVersion?.ToString() ?? "none"})");
        }

        var name = dto.Name?.Trim();
        if (string.IsNullOrEmpty(name) || !IsValidName(name))
        {
            errors.Add($"{file}: name is required (lowercase letters, digits, '-' or '_')");
        }
        else if (!seen.TryAdd(name, file))
        {
            errors.Add($"{file}: duplicate module name '{name}' (already defined in {seen[name]})");
        }

        var action = ModuleAction.Review;
        if (dto.DefaultAction is not null && !ModuleActions.TryParse(dto.DefaultAction, out action))
        {
            errors.Add($"{file}: default_action '{dto.DefaultAction}' is invalid (expected review, keep or ignore)");
        }

        var folderNames = (dto.Detect?.FolderNames ?? [])
            .Select(n => n.Trim()).Where(n => n.Length > 0).ToList();
        var extensions = (dto.Detect?.Extensions ?? [])
            .Select(e => e.Trim().TrimStart('.').ToLowerInvariant()).Where(e => e.Length > 0).ToList();

        foreach (var folder in folderNames)
        {
            if (folder.Contains('/') || folder.Contains('\\'))
            {
                errors.Add($"{file}: folder name '{folder}' must be a single path segment (no '/' or '\\')");
            }
        }

        if (folderNames.Count == 0 && extensions.Count == 0)
        {
            errors.Add($"{file}: detect must list at least one folder_name or extension");
        }

        var saveCarveBacks = dto.SaveCarveBacks ?? true;

        if (errors.Count > before || name is null)
        {
            return null;
        }

        return new ModuleDefinition(name, dto.Label?.Trim() ?? name, action, folderNames, extensions, saveCarveBacks);
    }

    private static bool IsValidName(string name)
    {
        foreach (var c in name)
        {
            if (!(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '_'))
            {
                return false;
            }
        }

        return name.Length > 0;
    }
}
