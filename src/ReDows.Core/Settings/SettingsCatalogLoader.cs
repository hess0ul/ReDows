using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ReDows.Core.Settings;

/// <summary>A settings catalog could not be loaded; every error is collected (fail-closed).</summary>
public sealed class SettingsCatalogException(IReadOnlyList<string> errors)
    : Exception($"Settings catalog invalid: {errors.Count} error(s).")
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>
/// Strict, fail-closed loader for the settings catalog (the data that drives the
/// registry reader). Unknown/duplicate YAML keys, a newer schema version, a bad
/// enum, a duplicate id, or a missing required field all abort loading — a typo
/// must never silently drop a setting from the read.
/// </summary>
public static partial class SettingsCatalogLoader
{
    public const int SupportedSchemaVersion = 1;

    [GeneratedRegex("^[a-z0-9]+([._-][a-z0-9]+)*$")]
    private static partial Regex IdPattern();

    public static SettingsCatalog LoadDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            throw new SettingsCatalogException([$"settings catalog directory '{directory}' does not exist"]);
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
                errors.Add($"{relative}: file unreadable ({ex.GetType().Name}) — refusing to load a partial catalog");
            }
        }

        if (errors.Count > 0)
        {
            throw new SettingsCatalogException(errors);
        }

        if (files.Count == 0)
        {
            throw new SettingsCatalogException([$"no .yaml/.yml settings files found in '{directory}'"]);
        }

        return LoadFiles(files);
    }

    public static SettingsCatalog LoadFiles(IEnumerable<(string Path, string Content)> files)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .WithDuplicateKeyChecking()
            .Build();

        var errors = new List<string>();
        var settings = new List<SettingDefinition>();
        var seenIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var schemaVersion = SupportedSchemaVersion;

        foreach (var (path, content) in files)
        {
            SettingsCatalogFileDto? dto;
            try
            {
                dto = deserializer.Deserialize<SettingsCatalogFileDto>(content);
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

            if (dto.Settings is null || dto.Settings.Count == 0)
            {
                errors.Add($"{path}: no 'settings'");
                continue;
            }

            foreach (var item in dto.Settings)
            {
                var def = Validate(path, item, errors);
                if (def is null)
                {
                    continue;
                }

                if (seenIds.TryGetValue(def.Id, out var firstFile))
                {
                    errors.Add($"{path}: duplicate setting id '{def.Id}' (also in {firstFile})");
                    continue;
                }

                seenIds[def.Id] = path;
                settings.Add(def);
            }
        }

        if (errors.Count > 0)
        {
            throw new SettingsCatalogException(errors);
        }

        return new SettingsCatalog(schemaVersion, settings);
    }

    private static SettingDefinition? Validate(string path, SettingDto dto, List<string> errors)
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
            Fail($"setting id '{dto.Id}' is missing or not in the form a-z0-9.-_");
            id = dto.Id ?? "<no-id>";
        }

        if (string.IsNullOrWhiteSpace(dto.Name)) Fail($"setting '{id}': missing 'name'");
        if (string.IsNullOrWhiteSpace(dto.What)) Fail($"setting '{id}': missing 'what'");

        var mechanism = ParseMechanism(dto.Mechanism);
        if (mechanism is null)
        {
            Fail($"setting '{id}': mechanism '{dto.Mechanism}' must be registry | optional_feature | capability | appx");
        }

        RegistryLocation? registry = null;
        string? selector = null;
        if (mechanism == SettingMechanism.Registry)
        {
            var hive = ParseHive(dto.Hive);
            if (hive is null) Fail($"setting '{id}': hive '{dto.Hive}' must be hklm | hkcu | hku_default");
            if (string.IsNullOrWhiteSpace(dto.Key)) Fail($"setting '{id}': missing 'key'");
            if (string.IsNullOrWhiteSpace(dto.Value)) Fail($"setting '{id}': missing 'value'");
            if (!IsKnownType(dto.Type)) Fail($"setting '{id}': type '{dto.Type}' must be dword|qword|sz|expand_sz|binary|multi_sz");
            if (hive is not null && !string.IsNullOrWhiteSpace(dto.Key) && !string.IsNullOrWhiteSpace(dto.Value))
            {
                registry = new RegistryLocation(hive.Value, dto.Key!, dto.Value!);
            }
        }
        else if (mechanism is { } m && NeedsSelector(m))
        {
            if (string.IsNullOrWhiteSpace(dto.Selector)) Fail($"setting '{id}': mechanism '{dto.Mechanism}' requires 'selector'");
            else selector = dto.Selector;
        }
        else if (!string.IsNullOrWhiteSpace(dto.Selector))
        {
            selector = dto.Selector; // network_profile / execution_policy: selector optional, kept if present
        }

        if (!ok || mechanism is null)
        {
            return null;
        }

        return new SettingDefinition(
            id!,
            dto.Name!,
            string.IsNullOrWhiteSpace(dto.Category) ? "misc" : dto.Category!,
            mechanism.Value,
            registry,
            selector,
            dto.What!,
            dto.InLoop ?? false,
            string.IsNullOrWhiteSpace(dto.IndowsModule) ? null : dto.IndowsModule,
            string.IsNullOrWhiteSpace(dto.Default) ? null : dto.Default,
            dto.Decode is { Count: > 0 } d ? new Dictionary<string, string>(d, StringComparer.OrdinalIgnoreCase) : EmptyDecode,
            string.IsNullOrWhiteSpace(dto.Source) ? null : dto.Source,
            string.IsNullOrWhiteSpace(dto.Note) ? null : dto.Note,
            dto.Applied ?? true);
    }

    private static SettingMechanism? ParseMechanism(string? mechanism) => mechanism switch
    {
        null or "" or "registry" => SettingMechanism.Registry,
        "optional_feature" => SettingMechanism.OptionalFeature,
        "capability" => SettingMechanism.Capability,
        "appx" => SettingMechanism.Appx,
        "network_profile" => SettingMechanism.NetworkProfile,
        "execution_policy" => SettingMechanism.ExecutionPolicy,
        "power_scheme" => SettingMechanism.PowerScheme,
        "power_setting" => SettingMechanism.PowerSetting,
        "scheduled_task" => SettingMechanism.ScheduledTask,
        _ => null,
    };

    private static bool NeedsSelector(SettingMechanism mechanism) =>
        mechanism is SettingMechanism.OptionalFeature or SettingMechanism.Capability
            or SettingMechanism.Appx or SettingMechanism.PowerSetting or SettingMechanism.ScheduledTask;

    private static readonly IReadOnlyDictionary<string, string> EmptyDecode =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private static SettingHive? ParseHive(string? hive) => hive switch
    {
        "hklm" => SettingHive.HkeyLocalMachine,
        "hkcu" => SettingHive.HkeyCurrentUser,
        "hku_default" => SettingHive.HkeyUsersDotDefault,
        _ => null,
    };

    private static bool IsKnownType(string? type) =>
        type is "dword" or "qword" or "sz" or "expand_sz" or "binary" or "multi_sz";
}
