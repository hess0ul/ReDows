using ReDows.Core.Settings;

namespace ReDows.Providers.Windows.Settings;

/// <summary>Result of the one-shot Windows feature/capability/appx queries (read-only). A null error string = ok.</summary>
public sealed record FeatureState(
    IReadOnlyDictionary<string, string> OptionalFeatures,
    string? OptionalFeaturesError,
    IReadOnlyDictionary<string, string> Capabilities,
    string? CapabilitiesError,
    IReadOnlySet<string> AppxNames,
    string? AppxError,
    IReadOnlyList<string> Notes);

/// <summary>
/// Reads optional-feature / capability / appx settings. Windows is queried ONCE per
/// kind (read-only PowerShell), then each catalog entry is resolved against the cache.
/// A query that fails (e.g. needs elevation) marks its settings unreadable with the
/// reason — never guessed (degraded gracefully, the ReDows invariant).
/// Optional features use the WMI Win32_OptionalFeature class, which is queryable
/// WITHOUT elevation (Get-WindowsOptionalFeature is not); InstallState 1=Enabled,
/// 2=Disabled, 3=Absent. Capabilities have no non-elevated path → elevation required.
/// </summary>
public static class FeaturesReader
{
    private const string FeaturesCommand =
        "Get-CimInstance -ClassName Win32_OptionalFeature | Select-Object Name,@{n='State';e={switch($_.InstallState){1{'Enabled'}2{'Disabled'}3{'Absent'}default{'Unknown'}}}} | ConvertTo-Json -Compress";
    private const string CapabilitiesCommand =
        "Get-WindowsCapability -Online | Select-Object Name,@{n='State';e={[string]$_.State}} | ConvertTo-Json -Compress";
    private const string AppxCommand =
        "Get-AppxPackage | Select-Object Name | ConvertTo-Json -Compress";

    private static readonly IReadOnlyDictionary<string, string> EmptyMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlySet<string> EmptySet =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public static FeatureState QueryAll(IReadOnlySet<SettingMechanism> needed)
    {
        var notes = new List<string>();
        var features = EmptyMap;
        string? featuresError = null;
        var capabilities = EmptyMap;
        string? capabilitiesError = null;
        var appx = EmptySet;
        string? appxError = null;

        if (needed.Contains(SettingMechanism.OptionalFeature))
        {
            var (output, reason) = PowerShellQuery.Run(FeaturesCommand);
            if (output is null) { featuresError = reason; notes.Add($"optional features: {reason}"); }
            else features = FeatureQuery.ParseStates(output, "Name");
        }

        if (needed.Contains(SettingMechanism.Capability))
        {
            var (output, reason) = PowerShellQuery.Run(CapabilitiesCommand);
            if (output is null) { capabilitiesError = reason; notes.Add($"capabilities: {reason}"); }
            else capabilities = FeatureQuery.ParseStates(output, "Name");
        }

        if (needed.Contains(SettingMechanism.Appx))
        {
            var (output, reason) = PowerShellQuery.Run(AppxCommand);
            if (output is null) { appxError = reason; notes.Add($"appx packages: {reason}"); }
            else appx = FeatureQuery.ParseNames(output, "Name");
        }

        return new FeatureState(features, featuresError, capabilities, capabilitiesError, appx, appxError, notes);
    }

    public static SettingReading Resolve(SettingDefinition definition, FeatureState state)
    {
        var selector = definition.Selector ?? string.Empty;
        return definition.Mechanism switch
        {
            SettingMechanism.OptionalFeature => state.OptionalFeaturesError is { } featuresError
                ? SettingDecoder.Decode(definition, rawValue: null, featuresError)
                : SettingDecoder.Decode(definition, state.OptionalFeatures.GetValueOrDefault(selector, "NotPresent")),
            SettingMechanism.Capability => state.CapabilitiesError is { } capabilitiesError
                ? SettingDecoder.Decode(definition, rawValue: null, capabilitiesError)
                : SettingDecoder.Decode(definition, state.Capabilities.GetValueOrDefault(selector, "NotPresent")),
            SettingMechanism.Appx => state.AppxError is { } appxError
                ? SettingDecoder.Decode(definition, rawValue: null, appxError)
                : SettingDecoder.Decode(definition, state.AppxNames.Contains(selector) ? "installed" : "removed"),
            _ => SettingDecoder.Decode(definition, rawValue: null, readError: "not a feature setting"),
        };
    }
}
