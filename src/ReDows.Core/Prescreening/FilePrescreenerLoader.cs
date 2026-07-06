using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ReDows.Core.Prescreening;

/// <summary>Raised when a present prescreen file is malformed. The fast path fails CLOSED, never silently wrong.</summary>
public sealed class FilePrescreenerValidationException(IReadOnlyList<string> errors)
    : Exception("Invalid file-prescreen rules:\n  " + string.Join("\n  ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>
/// Loads the fast-path rules from a directory of prescreen files (prescreen/*.yaml), merging their lists into
/// one <see cref="FilePrescreener"/>.
/// <para>Fail-SAFE on absence: a missing/empty directory yields <c>null</c>. No fast path, so every entry
/// simply goes to the AI exactly as before. The feature is a pure optimisation, so its absence is never an error.</para>
/// <para>Fail-CLOSED on corruption: a present but malformed file (bad YAML, wrong schema version) aborts the
/// load. A classifier the user relies on must work or say why it cannot.</para>
/// Prescreen lives OUTSIDE rules/ on purpose: it is a metadata classifier, not a fixed-verdict scan rule, so it
/// must never be swept into the ruleset (which would break the fail-closed rule loader and the rule count).
/// </summary>
public static class FilePrescreenerLoader
{
    public const int SupportedSchemaVersion = 1;

    public static FilePrescreener? LoadDirectory(string directory)
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
            throw new FilePrescreenerValidationException(errors);
        }

        return files.Count == 0 ? null : LoadFiles(files);
    }

    public static FilePrescreener LoadFiles(IEnumerable<(string Path, string Content)> files)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .WithDuplicateKeyChecking()
            .Build();

        var errors = new List<string>();
        List<string> keepExt = [], reviewExt = [], dropExt = [], secretExt = [], imageExt = [];
        List<string> secretNames = [], keepNames = [], keepFolders = [], dropFolders = [], cloudFolders = [];
        long thumbnailMaxBytes = 0;

        foreach (var (path, content) in files)
        {
            FilePrescreenerDto? dto;
            try
            {
                dto = deserializer.Deserialize<FilePrescreenerDto>(content);
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

            if (dto.ThumbnailMaxBytes is { } t && t > thumbnailMaxBytes)
            {
                thumbnailMaxBytes = t;
            }

            keepExt.AddRange(dto.Extensions?.Keep ?? []);
            reviewExt.AddRange(dto.Extensions?.Review ?? []);
            dropExt.AddRange(dto.Extensions?.Drop ?? []);
            secretExt.AddRange(dto.Extensions?.Secret ?? []);
            imageExt.AddRange(dto.Extensions?.Image ?? []);
            secretNames.AddRange(dto.Names?.Secret ?? []);
            keepNames.AddRange(dto.Names?.Keep ?? []);
            keepFolders.AddRange(dto.Folders?.Keep ?? []);
            dropFolders.AddRange(dto.Folders?.Drop ?? []);
            cloudFolders.AddRange(dto.Folders?.CloudSync ?? []);
        }

        if (errors.Count > 0)
        {
            throw new FilePrescreenerValidationException(errors);
        }

        return new FilePrescreener(
            keepExt, reviewExt, dropExt, secretExt, imageExt,
            secretNames, keepNames, keepFolders, dropFolders, cloudFolders, thumbnailMaxBytes);
    }

    private sealed class FilePrescreenerDto
    {
        public int? SchemaVersion { get; set; }

        public long? ThumbnailMaxBytes { get; set; }

        public ExtensionsDto? Extensions { get; set; }

        public NamesDto? Names { get; set; }

        public FoldersDto? Folders { get; set; }
    }

    private sealed class ExtensionsDto
    {
        public List<string>? Keep { get; set; }

        public List<string>? Review { get; set; }

        public List<string>? Drop { get; set; }

        public List<string>? Secret { get; set; }

        public List<string>? Image { get; set; }
    }

    private sealed class NamesDto
    {
        public List<string>? Secret { get; set; }

        public List<string>? Keep { get; set; }
    }

    private sealed class FoldersDto
    {
        public List<string>? Keep { get; set; }

        public List<string>? Drop { get; set; }

        public List<string>? CloudSync { get; set; }
    }
}
