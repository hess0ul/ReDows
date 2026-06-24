using System.Globalization;
using System.Security;
using Microsoft.Win32;
using ReDows.Core.Secrets;

namespace ReDows.Providers.Windows.Secrets;

/// <summary>
/// Reads one registry-secrets target from the LIVE registry (read-only).
/// <para>
/// HARD RULE (invariant #5 — secrets kept apart): a value is read with <c>GetValue</c>
/// ONLY after its name is classified <see cref="SecretClass.Config"/>. A name classified
/// secret (or unknown/review — which might be a secret) is recorded by NAME alone and its
/// value is never read. So config values (host, port, user) enrich the inventory while a
/// secret value is never read into memory.
/// </para>
/// V1 reads the current user's HKCU and the machine HKLM (64-bit view); other users'
/// offline NTUSER.DAT hives are a later increment (read-only, never <c>RegLoadKey</c>).
/// </summary>
public sealed class RegistrySecretsReader
{
    public RegistryObservation Read(RegistrySecretTarget target)
    {
        try
        {
            using var root = OpenRoot(target.Hive);
            using var key = root?.OpenSubKey(target.KeyPath, writable: false);
            if (key is null)
            {
                return RegistryObservation.Missing;
            }

            if (target.Shape == EntryShape.Values)
            {
                return new RegistryObservation(Present: true, Error: null, ReadValues(target, key), Subkeys: []);
            }

            var subkeys = new List<SubkeyObservation>();
            foreach (var name in key.GetSubKeyNames())
            {
                subkeys.Add(ReadSubkey(target, key, name));
            }

            return new RegistryObservation(Present: true, Error: null, Values: [], subkeys);
        }
        catch (Exception ex) when (IsAccessFailure(ex))
        {
            return RegistryObservation.Unreadable(Reason(ex, target.Hive));
        }
    }

    private static SubkeyObservation ReadSubkey(RegistrySecretTarget target, RegistryKey parent, string name)
    {
        try
        {
            using var sub = parent.OpenSubKey(name, writable: false);
            return new SubkeyObservation(name, sub is null ? [] : ReadValues(target, sub));
        }
        catch (Exception ex) when (IsAccessFailure(ex))
        {
            // One locked session: keep its name (counted), no values (never guessed).
            return new SubkeyObservation(name, []);
        }
    }

    private static IReadOnlyList<ObservedValue> ReadValues(RegistrySecretTarget target, RegistryKey key)
    {
        var values = new List<ObservedValue>();
        foreach (var name in key.GetValueNames())
        {
            values.Add(Observe(target, key, name));
        }

        return values;
    }

    /// <summary>Read a value ONLY if its name is config; secret/review names stay name-only.</summary>
    private static ObservedValue Observe(RegistrySecretTarget target, RegistryKey key, string name)
    {
        if (RegistrySecretProbe.ClassifyName(target, name) != SecretClass.Config)
        {
            return new ObservedValue(name); // secret or unknown → NAME ONLY, GetValue never called
        }

        try
        {
            var raw = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            return new ObservedValue(name, raw is null ? null : Normalize(raw));
        }
        catch (Exception ex) when (IsAccessFailure(ex))
        {
            return new ObservedValue(name); // a config value we couldn't read: name only
        }
    }

    /// <summary>Normalize a config value to one display string by its runtime type (DWORD/QWORD unsigned).</summary>
    private static string Normalize(object raw) => raw switch
    {
        string text => text,
        string[] multi => string.Join(";", multi),
        byte[] bytes => Convert.ToHexString(bytes),
        int dword => ((uint)dword).ToString(CultureInfo.InvariantCulture),
        long qword => ((ulong)qword).ToString(CultureInfo.InvariantCulture),
        _ => Convert.ToString(raw, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    private static RegistryKey? OpenRoot(SecretHive hive) => hive switch
    {
        SecretHive.Hklm => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64),
        SecretHive.Hkcu => RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default),
        _ => null,
    };

    private static bool IsAccessFailure(Exception ex) =>
        ex is SecurityException or UnauthorizedAccessException or IOException or ArgumentException;

    private static string Reason(Exception ex, SecretHive hive) =>
        ex is SecurityException or UnauthorizedAccessException
            ? (hive == SecretHive.Hklm ? "requires elevation" : "access denied")
            : ex.GetType().Name;
}
