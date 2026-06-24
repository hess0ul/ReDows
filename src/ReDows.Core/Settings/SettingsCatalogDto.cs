using Json.Schema.Generation;

namespace ReDows.Core.Settings;

// YAML shape of a settings catalog file (snake_case keys): the list of Windows
// settings ReDows reads from the registry (block 3, reader R1). These DTOs are
// the schema source of truth; the strict loader enforces what a schema cannot.

[Title("ReDows settings catalog file")]
[Description("A schema version plus the list of Windows settings ReDows reads from the registry.")]
public sealed class SettingsCatalogFileDto
{
    [Required]
    [Description("Catalog schema version understood by the engine. A newer version is refused (fail-closed).")]
    public int? SchemaVersion { get; set; }

    [Required]
    [Description("The settings to read.")]
    public List<SettingDto>? Settings { get; set; }
}

[Title("Setting")]
public sealed class SettingDto
{
    [Required]
    [Pattern("^[a-z0-9]+([._-][a-z0-9]+)*$")]
    [Description("Stable unique identifier.")]
    public string? Id { get; set; }

    [Required]
    [Description("Short human name.")]
    public string? Name { get; set; }

    [Description("Grouping (identity, locale, ui, privacy, network, power, startup, services, features, security, misc).")]
    public string? Category { get; set; }

    [Pattern("^(registry|optional_feature|capability|appx|network_profile|execution_policy|power_scheme|power_setting|scheduled_task)$")]
    [Description("How the setting is read. Default: registry. The others query Windows (read-only). optional_feature/capability/appx/power_setting/scheduled_task need a selector; network_profile/execution_policy/power_scheme are global.")]
    public string? Mechanism { get; set; }

    [Pattern("^(hklm|hkcu|hku_default)$")]
    [Description(@"Registry root (mechanism=registry): hklm (machine, 64-bit view), hkcu (current user), hku_default (HKU\.DEFAULT).")]
    public string? Hive { get; set; }

    [Description("Registry key path under the hive, no leading backslash (mechanism=registry).")]
    public string? Key { get; set; }

    [Description("Registry value name to read (mechanism=registry).")]
    public string? Value { get; set; }

    [Pattern("^(dword|qword|sz|expand_sz|binary|multi_sz)$")]
    [Description("Declared registry value type (mechanism=registry; reading normalizes the actual runtime type).")]
    public string? Type { get; set; }

    [Description("Feature/capability/package name to look up (mechanism=optional_feature/capability/appx).")]
    public string? Selector { get; set; }

    [Required]
    [Description("What the setting controls (one line).")]
    public string? What { get; set; }

    [Description("True if an InDows module can re-apply this setting (part of the ReDows→InDows loop).")]
    public bool? InLoop { get; set; }

    [Description("How the setting comes back after a reset: an existing module name, 'NEW:<module>' to build, or a sentinel — 'base' (InDows base install applies it), 'perso' (only the private config does), 'none' (nothing does, redo by hand). Blank = unmapped.")]
    public string? IndowsModule { get; set; }

    [Description("True (default) when the named module actually applies this today; false when the module is the intended home but the line is commented/planned (not applied yet).")]
    public bool? Applied { get; set; }

    [Description("Normalized value when the registry value is absent (the Windows default). Optional.")]
    public string? Default { get; set; }

    [Description("Optional raw-value → human-meaning map (keys are normalized string values).")]
    public Dictionary<string, string>? Decode { get; set; }

    [Description("Provenance.")]
    public string? Source { get; set; }

    [Description("Free-form note.")]
    public string? Note { get; set; }
}
