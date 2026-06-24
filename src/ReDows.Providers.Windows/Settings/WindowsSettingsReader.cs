using ReDows.Core.Settings;

namespace ReDows.Providers.Windows.Settings;

/// <summary>
/// Reads the whole settings catalog (read-only) by dispatching each setting to the
/// right mechanism: registry values one by one, and feature/network settings against
/// a single up-front Windows query each. Produces one merged report in catalog order.
/// </summary>
public static class WindowsSettingsReader
{
    public static SettingsReport Read(SettingsCatalog catalog)
    {
        var mechanisms = catalog.Settings.Select(s => s.Mechanism).ToHashSet();

        var featureState = mechanisms.Overlaps(FeatureMechanisms)
            ? FeaturesReader.QueryAll(mechanisms)
            : null;
        var networkState = mechanisms.Overlaps(NetworkMechanisms)
            ? NetworkReader.QueryAll(mechanisms)
            : null;
        var tasksState = mechanisms.Contains(SettingMechanism.ScheduledTask)
            ? TasksReader.Query()
            : null;

        var readings = new List<SettingReading>(catalog.Settings.Count);
        foreach (var definition in catalog.Settings)
        {
            readings.Add(definition.Mechanism switch
            {
                SettingMechanism.Registry => RegistrySettingsReader.ReadOne(definition),
                SettingMechanism.NetworkProfile or SettingMechanism.ExecutionPolicy => NetworkReader.Resolve(definition, networkState!),
                SettingMechanism.PowerScheme or SettingMechanism.PowerSetting => PowerReader.Resolve(definition),
                SettingMechanism.ScheduledTask => TasksReader.Resolve(definition, tasksState!),
                _ => FeaturesReader.Resolve(definition, featureState!),
            });
        }

        var notes = (featureState?.Notes ?? [])
            .Concat(networkState?.Notes ?? [])
            .Concat(tasksState?.Notes ?? [])
            .ToList();
        return new SettingsReport(readings, notes, SettingsReport.V1Limits);
    }

    private static readonly SettingMechanism[] FeatureMechanisms =
        [SettingMechanism.OptionalFeature, SettingMechanism.Capability, SettingMechanism.Appx];

    private static readonly SettingMechanism[] NetworkMechanisms =
        [SettingMechanism.NetworkProfile, SettingMechanism.ExecutionPolicy];
}
