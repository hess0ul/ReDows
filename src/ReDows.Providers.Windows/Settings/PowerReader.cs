using ReDows.Core.Settings;

namespace ReDows.Providers.Windows.Settings;

/// <summary>
/// Reads power settings via the read-only <c>powercfg</c> query commands (no
/// elevation): the active scheme name (<c>/getactivescheme</c>) and a specific
/// setting's AC index (<c>/query SCHEME_CURRENT &lt;selector&gt;</c>, e.g.
/// "SUB_VIDEO VIDEOIDLE"). Only a couple power settings exist, so each is queried
/// on demand; the text parsing is pure (Core <see cref="PowerOutput"/>).
/// </summary>
public static class PowerReader
{
    public static SettingReading Resolve(SettingDefinition definition) => definition.Mechanism switch
    {
        SettingMechanism.PowerScheme =>
            Read(definition, "powercfg /getactivescheme", PowerOutput.ActiveSchemeName),
        SettingMechanism.PowerSetting =>
            Read(definition, $"powercfg /query SCHEME_CURRENT {definition.Selector}", PowerOutput.AcSettingIndex),
        _ => SettingDecoder.Decode(definition, rawValue: null, readError: "not a power setting"),
    };

    private static SettingReading Read(SettingDefinition definition, string command, Func<string?, string?> parse)
    {
        var (output, reason) = PowerShellQuery.Run(command);
        return output is null
            ? SettingDecoder.Decode(definition, rawValue: null, reason)
            : SettingDecoder.Decode(definition, parse(output));
    }
}
