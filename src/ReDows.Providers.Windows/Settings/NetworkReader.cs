using ReDows.Core.Settings;

namespace ReDows.Providers.Windows.Settings;

/// <summary>Result of the one-shot network/execution-policy queries (read-only). A null error string = ok.</summary>
public sealed record NetworkState(
    string? Profiles,
    string? ProfilesError,
    string? ExecutionPolicy,
    string? ExecutionPolicyError,
    IReadOnlyList<string> Notes);

/// <summary>
/// Reads network-profile and PowerShell-execution-policy settings (read-only, no
/// elevation). The connection profile (Private/Public per adapter) comes from
/// Get-NetConnectionProfile; the execution policy is the EFFECTIVE value
/// (Get-ExecutionPolicy, precedence-aware) — distinct from the LocalMachine
/// registry value, which any narrower scope or GPO can override.
/// </summary>
public static class NetworkReader
{
    private const string ProfilesCommand =
        "Get-NetConnectionProfile | Select-Object InterfaceAlias,@{n='State';e={[string]$_.NetworkCategory}} | ConvertTo-Json -Compress";
    private const string ExecutionPolicyCommand = "[string](Get-ExecutionPolicy)";

    public static NetworkState QueryAll(IReadOnlySet<SettingMechanism> needed)
    {
        var notes = new List<string>();
        string? profiles = null;
        string? profilesError = null;
        string? executionPolicy = null;
        string? executionPolicyError = null;

        if (needed.Contains(SettingMechanism.NetworkProfile))
        {
            var (output, reason) = PowerShellQuery.Run(ProfilesCommand);
            if (output is null) { profilesError = reason; notes.Add($"network profiles: {reason}"); }
            else profiles = SummarizeProfiles(FeatureQuery.ParseStates(output, "InterfaceAlias"));
        }

        if (needed.Contains(SettingMechanism.ExecutionPolicy))
        {
            var (output, reason) = PowerShellQuery.Run(ExecutionPolicyCommand);
            if (output is null) { executionPolicyError = reason; notes.Add($"execution policy: {reason}"); }
            else executionPolicy = output.Trim();
        }

        return new NetworkState(profiles, profilesError, executionPolicy, executionPolicyError, notes);
    }

    public static SettingReading Resolve(SettingDefinition definition, NetworkState state) =>
        definition.Mechanism switch
        {
            SettingMechanism.NetworkProfile => state.ProfilesError is { } profilesError
                ? SettingDecoder.Decode(definition, rawValue: null, profilesError)
                : SettingDecoder.Decode(definition, state.Profiles),
            SettingMechanism.ExecutionPolicy => state.ExecutionPolicyError is { } executionPolicyError
                ? SettingDecoder.Decode(definition, rawValue: null, executionPolicyError)
                : SettingDecoder.Decode(definition, string.IsNullOrEmpty(state.ExecutionPolicy) ? null : state.ExecutionPolicy),
            _ => SettingDecoder.Decode(definition, rawValue: null, readError: "not a network setting"),
        };

    private static string SummarizeProfiles(IReadOnlyDictionary<string, string> aliasToCategory) =>
        aliasToCategory.Count == 0
            ? "(no active network profile)"
            : string.Join("; ", aliasToCategory
                .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .Select(p => $"{p.Key}:{p.Value}"));
}
