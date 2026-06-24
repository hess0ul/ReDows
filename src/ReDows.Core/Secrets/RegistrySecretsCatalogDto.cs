using Json.Schema.Generation;

namespace ReDows.Core.Secrets;

// YAML shape of the registry-secrets catalog (snake_case keys): the registry locations
// ReDows inspects for app secrets/config (block 7). The strict loader enforces what a
// schema cannot (duplicate ids, unknown enums, a newer schema version).

[Title("ReDows registry-secrets catalog file")]
[Description("A schema version plus the registry locations ReDows inspects (read-only, names only) for app secrets and config.")]
public sealed class RegistrySecretsCatalogFileDto
{
    [Required]
    [Description("Catalog schema version understood by the engine. A newer version is refused (fail-closed).")]
    public int? SchemaVersion { get; set; }

    [Required]
    [Description("The registry locations to inspect.")]
    public List<RegistrySecretTargetDto>? Targets { get; set; }
}

[Title("Registry secrets target")]
public sealed class RegistrySecretTargetDto
{
    [Required]
    [Pattern("^[a-z0-9]+([._-][a-z0-9]+)*$")]
    [Description("Stable unique identifier.")]
    public string? Id { get; set; }

    [Required]
    [Description("Application this location belongs to.")]
    public string? App { get; set; }

    [Required]
    [Pattern("^(hkcu|hklm)$")]
    [Description("Registry root: hkcu (per-user) or hklm (machine, 64-bit view).")]
    public string? Hive { get; set; }

    [Required]
    [Description("Registry key path under the hive, no leading backslash.")]
    public string? Key { get; set; }

    [Required]
    [Pattern("^(subkeys|values)$")]
    [Description("How saved items are laid out: 'subkeys' (one subkey per session) or 'values' (one value per item).")]
    public string? Shape { get; set; }

    [Description("Value NAMES that hold a secret (e.g. Password). Recorded by location, never read.")]
    public List<string>? SecretValues { get; set; }

    [Description("Value NAMES that hold reconstructable config worth keeping (host, port, user).")]
    public List<string>? ConfigValues { get; set; }

    [Pattern("^(config|review)$")]
    [Description("How to treat a value NAME not in either list: 'config' (this key's names are inherently config, e.g. host keys / MRU) or 'review' (default — unknown name surfaced, never dropped).")]
    public string? Unmatched { get; set; }

    [Pattern("^(config_only|reversible_secret|strong_secret)$")]
    [Description("How recoverable the secret is (drives the pre-reset alert). Default: config_only.")]
    public string? Sensitivity { get; set; }

    [Required]
    [Description("What this location holds (one line).")]
    public string? What { get; set; }

    [Description("Optional human hint: how to export this before the reset.")]
    public string? ExportHint { get; set; }
}
