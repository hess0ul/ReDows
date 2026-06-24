namespace ReDows.Core.Secrets;

/// <summary>Registry root a secrets target lives under (V1: per-user HKCU, machine HKLM).</summary>
public enum SecretHive
{
    Hkcu,
    Hklm,
}

/// <summary>
/// How saved items are laid out under the key: one SUBKEY per item (PuTTY/WinSCP
/// sessions) or one VALUE per item (PuTTY SshHostKeys, Remote-Desktop MRU).
/// </summary>
public enum EntryShape
{
    Subkeys,
    Values,
}

/// <summary>
/// How recoverable a target's secret is — drives the pre-reset alert wording.
/// </summary>
public enum SecretSensitivity
{
    /// <summary>No secret here, only reconstructable config (PuTTY sessions, RDP MRU).</summary>
    ConfigOnly,

    /// <summary>The stored secret is recoverable from the registry alone (WinSCP obfuscation, VNC hash).</summary>
    ReversibleSecret,

    /// <summary>Protected (DPAPI / master password): unreadable after a reset — export it beforehand.</summary>
    StrongSecret,
}

/// <summary>
/// One app location ReDows inspects for registry-resident secrets/config. The reader
/// is READ-ONLY and works on value NAMES only — a value classified <see cref="SecretClass.Secret"/>
/// is recorded by its location, never read (invariant: secrets kept apart, never in clear).
/// </summary>
public sealed record RegistrySecretTarget(
    string Id,
    string App,
    SecretHive Hive,
    string KeyPath,
    EntryShape Shape,
    IReadOnlySet<string> SecretValueNames,
    IReadOnlySet<string> ConfigValueNames,
    SecretSensitivity Sensitivity,
    string What,
    string? ExportHint,
    SecretClass Unmatched = SecretClass.Review);

/// <summary>The loaded, validated catalog of registry locations to inspect.</summary>
public sealed record RegistrySecretsCatalog(int SchemaVersion, IReadOnlyList<RegistrySecretTarget> Targets);
