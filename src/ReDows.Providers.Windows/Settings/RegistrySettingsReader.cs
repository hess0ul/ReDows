using System.Globalization;
using System.Security;
using Microsoft.Win32;
using ReDows.Core.Settings;

namespace ReDows.Providers.Windows.Settings;

/// <summary>
/// Reads one registry-mechanism setting (read-only). V1 hives: HKLM (64-bit view),
/// HKCU (current user), HKU\.DEFAULT. An absent key or value is reported as "at
/// default" (never an error); a denied read is counted with its reason. The pure
/// decode/default logic lives in Core (<see cref="SettingDecoder"/>).
/// </summary>
public static class RegistrySettingsReader
{
    public static SettingReading ReadOne(SettingDefinition definition)
    {
        if (definition.Registry is not { } location)
        {
            return SettingDecoder.Decode(definition, rawValue: null, readError: "registry setting has no location");
        }

        try
        {
            using var root = OpenRoot(location.Hive);
            using var key = root?.OpenSubKey(location.Key);
            if (key is null)
            {
                // Key absent ⇒ value absent ⇒ the setting sits at its default.
                return SettingDecoder.Decode(definition, rawValue: null);
            }

            var raw = key.GetValue(location.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            return SettingDecoder.Decode(definition, raw is null ? null : Normalize(raw));
        }
        catch (SecurityException)
        {
            return SettingDecoder.Decode(definition, rawValue: null, readError: "access denied (no elevation)");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return SettingDecoder.Decode(definition, rawValue: null, readError: ex.GetType().Name);
        }
    }

    private static RegistryKey? OpenRoot(SettingHive hive) => hive switch
    {
        SettingHive.HkeyLocalMachine => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64),
        SettingHive.HkeyCurrentUser => RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default),
        SettingHive.HkeyUsersDotDefault => Registry.Users.OpenSubKey(".DEFAULT"),
        _ => null,
    };

    /// <summary>
    /// Normalize a registry value to a single string by its actual runtime type.
    /// DWORD/QWORD are presented UNSIGNED (the registry's semantics): 0xFFFFFFFF
    /// reads back as Int32 -1 but means 4294967295.
    /// </summary>
    private static string Normalize(object raw) => raw switch
    {
        byte[] bytes => Convert.ToHexString(bytes),
        string[] multi => string.Join(";", multi),
        string text => text,
        int dword => ((uint)dword).ToString(CultureInfo.InvariantCulture),
        long qword => ((ulong)qword).ToString(CultureInfo.InvariantCulture),
        _ => Convert.ToString(raw, CultureInfo.InvariantCulture) ?? string.Empty,
    };
}
