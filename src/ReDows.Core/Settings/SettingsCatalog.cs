namespace ReDows.Core.Settings;

/// <summary>Registry root a setting lives under (V1: machine, current user, sign-in default).</summary>
public enum SettingHive
{
    HkeyLocalMachine,
    HkeyCurrentUser,
    HkeyUsersDotDefault,
}

/// <summary>How a setting is read. Registry is the default; the others query Windows once.</summary>
public enum SettingMechanism
{
    Registry,
    OptionalFeature,
    Capability,
    Appx,
    NetworkProfile,
    ExecutionPolicy,
    PowerScheme,
    PowerSetting,
    ScheduledTask,
}

/// <summary>Where a registry setting lives (set when <see cref="SettingDefinition.Mechanism"/> is Registry).</summary>
public sealed record RegistryLocation(SettingHive Hive, string Key, string ValueName);

/// <summary>One Windows setting ReDows knows how to read (block 3).</summary>
/// <remarks>
/// <see cref="IndowsModule"/> names how the setting comes back after a reset: an existing module,
/// a <c>NEW:</c>-prefixed module to build, or one of the sentinels <c>base</c> (the InDows base
/// install applies it), <c>perso</c> (only the private config does), <c>none</c> (nothing does —
/// redo by hand). <see cref="Applied"/> is false when a real module is the intended home but the
/// line is commented/planned and not applied yet.
/// </remarks>
public sealed record SettingDefinition(
    string Id,
    string Name,
    string Category,
    SettingMechanism Mechanism,
    RegistryLocation? Registry,
    string? Selector,
    string What,
    bool InLoop,
    string? IndowsModule,
    string? Default,
    IReadOnlyDictionary<string, string> Decode,
    string? Source,
    string? Note,
    bool Applied = true);

/// <summary>The loaded, validated catalog of settings to read.</summary>
public sealed record SettingsCatalog(int SchemaVersion, IReadOnlyList<SettingDefinition> Settings);

/// <summary>
/// One setting after reading: present (a registry value existed) or absent (taken
/// at its documented default), with a human-readable meaning, and whether the read
/// value equals the Windows default. <see cref="Error"/> is set when the read failed.
/// </summary>
public sealed record SettingReading(
    SettingDefinition Definition,
    bool Present,
    string? RawValue,
    string Meaning,
    bool IsDefault,
    string? Error);

public sealed record SettingsReport(
    IReadOnlyList<SettingReading> Readings,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string> DeclaredLimits)
{
    /// <summary>Stated rather than hidden: what this V1 registry reader does NOT cover.</summary>
    public static readonly IReadOnlyList<string> V1Limits =
    [
        "Only the current user's HKCU and the machine HKLM (64-bit view) are read; other users' hives (offline NTUSER.DAT) and the 32-bit view arrive with later blocks.",
        "Settings with no registry mechanism (default browser, custom power plan, Storage Sense action, Paint/Photos actions) are out of scope of this registry reader.",
        "A value read 'at its default' means the registry value is absent — some settings only materialize once the user changes them.",
    ];
}

/// <summary>
/// Pure mapping of a read raw value (already normalized to a string by the provider,
/// or null when absent) to a <see cref="SettingReading"/>: applies the catalog's
/// decode map and default. No registry access — testable on fixtures.
/// </summary>
public static class SettingDecoder
{
    public static SettingReading Decode(SettingDefinition definition, string? rawValue, string? readError = null)
    {
        if (readError is not null)
        {
            return new SettingReading(definition, Present: false, RawValue: null, $"(unreadable: {readError})", IsDefault: false, readError);
        }

        if (rawValue is null)
        {
            // Absent registry value = the Windows default.
            var meaning = definition.Default is not null
                ? $"{Lookup(definition, definition.Default)} (absent → default)"
                : "(absent — no documented default)";
            return new SettingReading(definition, Present: false, RawValue: null, meaning, IsDefault: definition.Default is not null, Error: null);
        }

        var isDefault = definition.Default is not null
            && string.Equals(rawValue, definition.Default, StringComparison.OrdinalIgnoreCase);
        return new SettingReading(definition, Present: true, rawValue, Lookup(definition, rawValue), isDefault, Error: null);
    }

    private static string Lookup(SettingDefinition definition, string value) =>
        definition.Decode.TryGetValue(value, out var meaning) ? meaning : value;
}
