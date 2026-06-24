using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ReDows.Core.Secrets;

/// <summary>A registry-secrets catalog could not be loaded; every error is collected (fail-closed).</summary>
public sealed class RegistrySecretsCatalogException(IReadOnlyList<string> errors)
    : Exception($"Registry-secrets catalog invalid: {errors.Count} error(s).")
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>
/// Strict, fail-closed loader for the registry-secrets catalog. Unknown/duplicate YAML
/// keys, a newer schema version, a bad enum, a duplicate id, or a missing required field
/// all abort loading — a typo must never silently drop a secrets location from the pass.
/// </summary>
public static partial class RegistrySecretsCatalogLoader
{
    public const int SupportedSchemaVersion = 1;

    /// <summary>A registry key path far longer than any real one is a catalog typo (refused fail-closed).</summary>
    private const int MaxKeyLength = 512;

    [GeneratedRegex("^[a-z0-9]+([._-][a-z0-9]+)*$")]
    private static partial Regex IdPattern();

    public static RegistrySecretsCatalog LoadDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            throw new RegistrySecretsCatalogException([$"registry-secrets catalog directory '{directory}' does not exist"]);
        }

        var paths = Directory.EnumerateFiles(directory, "*.yaml", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(directory, "*.yml", SearchOption.AllDirectories))
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
                errors.Add($"{relative}: file unreadable ({ex.GetType().Name}) — refusing to load a partial catalog");
            }
        }

        if (errors.Count > 0)
        {
            throw new RegistrySecretsCatalogException(errors);
        }

        if (files.Count == 0)
        {
            throw new RegistrySecretsCatalogException([$"no .yaml/.yml files found in '{directory}'"]);
        }

        return LoadFiles(files);
    }

    public static RegistrySecretsCatalog LoadFiles(IEnumerable<(string Path, string Content)> files)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .WithDuplicateKeyChecking()
            .Build();

        var errors = new List<string>();
        var targets = new List<RegistrySecretTarget>();
        var seenIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var schemaVersion = SupportedSchemaVersion;

        foreach (var (path, content) in files)
        {
            RegistrySecretsCatalogFileDto? dto;
            try
            {
                dto = deserializer.Deserialize<RegistrySecretsCatalogFileDto>(content);
            }
            catch (YamlException ex)
            {
                errors.Add($"{path}: YAML error — {ex.Message}");
                continue;
            }

            if (dto is null)
            {
                errors.Add($"{path}: empty file");
                continue;
            }

            if (dto.SchemaVersion is not { } version)
            {
                errors.Add($"{path}: missing 'schema_version'");
            }
            else if (version > SupportedSchemaVersion)
            {
                errors.Add($"{path}: schema_version {version} is newer than supported {SupportedSchemaVersion} (fail-closed)");
            }
            else
            {
                schemaVersion = version;
            }

            if (dto.Targets is null || dto.Targets.Count == 0)
            {
                errors.Add($"{path}: no 'targets'");
                continue;
            }

            foreach (var item in dto.Targets)
            {
                var target = Validate(path, item, errors);
                if (target is null)
                {
                    continue;
                }

                if (seenIds.TryGetValue(target.Id, out var firstFile))
                {
                    errors.Add($"{path}: duplicate target id '{target.Id}' (also in {firstFile})");
                    continue;
                }

                seenIds[target.Id] = path;
                targets.Add(target);
            }
        }

        if (errors.Count > 0)
        {
            throw new RegistrySecretsCatalogException(errors);
        }

        return new RegistrySecretsCatalog(schemaVersion, targets);
    }

    private static RegistrySecretTarget? Validate(string path, RegistrySecretTargetDto dto, List<string> errors)
    {
        var ok = true;
        void Fail(string message)
        {
            errors.Add($"{path}: {message}");
            ok = false;
        }

        var id = dto.Id;
        if (string.IsNullOrWhiteSpace(id) || !IdPattern().IsMatch(id))
        {
            Fail($"target id '{dto.Id}' is missing or not in the form a-z0-9.-_");
            id = dto.Id ?? "<no-id>";
        }

        if (string.IsNullOrWhiteSpace(dto.App)) Fail($"target '{id}': missing 'app'");
        if (string.IsNullOrWhiteSpace(dto.Key)) Fail($"target '{id}': missing 'key'");
        else if (dto.Key.Length > MaxKeyLength) Fail($"target '{id}': key is {dto.Key.Length} chars (max {MaxKeyLength})");
        if (string.IsNullOrWhiteSpace(dto.What)) Fail($"target '{id}': missing 'what'");

        var hive = ParseHive(dto.Hive);
        if (hive is null) Fail($"target '{id}': hive '{dto.Hive}' must be hkcu | hklm");

        var shape = ParseShape(dto.Shape);
        if (shape is null) Fail($"target '{id}': shape '{dto.Shape}' must be subkeys | values");

        var sensitivity = ParseSensitivity(dto.Sensitivity);
        if (sensitivity is null) Fail($"target '{id}': sensitivity '{dto.Sensitivity}' must be config_only | reversible_secret | strong_secret");

        var unmatched = ParseUnmatched(dto.Unmatched);
        if (unmatched is null) Fail($"target '{id}': unmatched '{dto.Unmatched}' must be config | review");

        if (!ok || hive is null || shape is null || sensitivity is null || unmatched is null)
        {
            return null;
        }

        return new RegistrySecretTarget(
            id!,
            dto.App!,
            hive.Value,
            dto.Key!,
            shape.Value,
            ToSet(dto.SecretValues),
            ToSet(dto.ConfigValues),
            sensitivity.Value,
            dto.What!,
            string.IsNullOrWhiteSpace(dto.ExportHint) ? null : dto.ExportHint,
            unmatched.Value);
    }

    private static IReadOnlySet<string> ToSet(List<string>? names) =>
        names is { Count: > 0 }
            ? new HashSet<string>(names, StringComparer.OrdinalIgnoreCase)
            : EmptyNames;

    private static readonly IReadOnlySet<string> EmptyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private static SecretHive? ParseHive(string? hive) => hive switch
    {
        "hkcu" => SecretHive.Hkcu,
        "hklm" => SecretHive.Hklm,
        _ => null,
    };

    private static EntryShape? ParseShape(string? shape) => shape switch
    {
        "subkeys" => EntryShape.Subkeys,
        "values" => EntryShape.Values,
        _ => null,
    };

    private static SecretSensitivity? ParseSensitivity(string? sensitivity) => sensitivity switch
    {
        null or "" or "config_only" => SecretSensitivity.ConfigOnly,
        "reversible_secret" => SecretSensitivity.ReversibleSecret,
        "strong_secret" => SecretSensitivity.StrongSecret,
        _ => null,
    };

    private static SecretClass? ParseUnmatched(string? unmatched) => unmatched switch
    {
        null or "" or "review" => SecretClass.Review,
        "config" => SecretClass.Config,
        _ => null,
    };
}
