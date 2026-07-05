using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ReDows.Core.Memory;

/// <summary>Raised when a present memory file is malformed — the memory fails CLOSED, never silently wrong.</summary>
public sealed class MemoryValidationException(IReadOnlyList<string> errors)
    : Exception("Invalid folder-memory:\n  " + string.Join("\n  ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>
/// Loads ReDows' folder memory from a directory of memory files (memory/*.yaml), merging their entries
/// into one <see cref="FolderMemory"/>.
/// <para>Fail-SAFE on absence: a missing/empty directory yields <c>null</c> — no memory, so nothing is
/// recognised and the scan behaves as before. The memory is an overlay, so its absence is never an error.</para>
/// <para>Fail-CLOSED on corruption: a present but malformed file (bad YAML, wrong schema version, an
/// entry missing its match or note, an invalid importance) aborts the load.</para>
/// </summary>
public static class MemoryLoader
{
    public const int SupportedSchemaVersion = 1;

    private static readonly HashSet<string> Importances = new(StringComparer.OrdinalIgnoreCase) { "keep", "maybe", "drop" };

    private static readonly HashSet<string> Scopes = new(StringComparer.OrdinalIgnoreCase) { "subtree", "self" };

    public static FolderMemory? LoadDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        var paths = Directory.EnumerateFiles(directory, "*.yaml", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(directory, "*.yml", SearchOption.AllDirectories))
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
            throw new MemoryValidationException(errors);
        }

        return files.Count == 0 ? null : LoadFiles(files);
    }

    public static FolderMemory LoadFiles(IEnumerable<(string Path, string Content)> files)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .WithDuplicateKeyChecking()
            .Build();

        var errors = new List<string>();
        var entries = new List<KnownEntry>();

        foreach (var (path, content) in files)
        {
            MemoryFileDto? dto;
            try
            {
                dto = deserializer.Deserialize<MemoryFileDto>(content);
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

            if (dto.SchemaVersion != SupportedSchemaVersion)
            {
                errors.Add($"{path}: schema_version must be {SupportedSchemaVersion} (got {dto.SchemaVersion?.ToString() ?? "none"})");
                continue;
            }

            foreach (var entry in dto.Known ?? [])
            {
                var match = entry.Match?.Trim();
                var note = entry.Note?.Trim();
                if (string.IsNullOrEmpty(match))
                {
                    errors.Add($"{path}: an entry is missing its 'match'");
                    continue;
                }

                if (string.IsNullOrEmpty(note))
                {
                    errors.Add($"{path}: '{match}' is missing its 'note'");
                    continue;
                }

                var importance = entry.Importance?.Trim();
                if (importance is not null && !Importances.Contains(importance))
                {
                    errors.Add($"{path}: '{match}' has an invalid importance '{importance}' (expected keep, maybe or drop)");
                    continue;
                }

                var scope = entry.Scope?.Trim();
                if (scope is not null && !Scopes.Contains(scope))
                {
                    errors.Add($"{path}: '{match}' has an invalid scope '{scope}' (expected subtree or self)");
                    continue;
                }

                entries.Add(new KnownEntry(match, importance?.ToLowerInvariant(), note, scope?.ToLowerInvariant() ?? "subtree"));
            }
        }

        if (errors.Count > 0)
        {
            throw new MemoryValidationException(errors);
        }

        return new FolderMemory(entries);
    }

    private sealed class MemoryFileDto
    {
        public int? SchemaVersion { get; set; }

        public List<KnownEntryDto>? Known { get; set; }
    }

    private sealed class KnownEntryDto
    {
        public string? Match { get; set; }

        public string? Importance { get; set; }

        public string? Note { get; set; }

        public string? Scope { get; set; }
    }
}
