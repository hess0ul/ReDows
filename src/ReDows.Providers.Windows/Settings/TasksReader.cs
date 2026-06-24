using ReDows.Core.Settings;

namespace ReDows.Providers.Windows.Settings;

/// <summary>Result of the one-shot scheduled-task query (read-only). A null error string = ok.</summary>
public sealed record TasksState(IReadOnlyDictionary<string, string> Tasks, string? Error, IReadOnlyList<string> Notes);

/// <summary>
/// Reads scheduled-task settings: the state (Ready/Disabled/Running) of specific
/// tasks named by their full path (TaskPath + TaskName). Get-ScheduledTask is
/// queried ONCE (read-only, no elevation), then each catalog entry is resolved by
/// its selector; a task absent from the result reads as "NotPresent".
/// </summary>
public static class TasksReader
{
    private const string Command =
        "Get-ScheduledTask | Select-Object @{n='Name';e={$_.TaskPath+$_.TaskName}},@{n='State';e={[string]$_.State}} | ConvertTo-Json -Compress";

    private static readonly IReadOnlyDictionary<string, string> EmptyMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public static TasksState Query()
    {
        var (output, reason) = PowerShellQuery.Run(Command);
        return output is null
            ? new TasksState(EmptyMap, reason, [$"scheduled tasks: {reason}"])
            : new TasksState(FeatureQuery.ParseStates(output, "Name"), null, []);
    }

    public static SettingReading Resolve(SettingDefinition definition, TasksState state)
    {
        if (state.Error is { } error)
        {
            return SettingDecoder.Decode(definition, rawValue: null, error);
        }

        var selector = definition.Selector ?? string.Empty;
        return SettingDecoder.Decode(definition, state.Tasks.GetValueOrDefault(selector, "NotPresent"));
    }
}
