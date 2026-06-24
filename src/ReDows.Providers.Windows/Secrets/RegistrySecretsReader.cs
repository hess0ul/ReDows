using System.Security;
using Microsoft.Win32;
using ReDows.Core.Secrets;

namespace ReDows.Providers.Windows.Secrets;

/// <summary>
/// Reads one registry-secrets target from the LIVE registry (read-only) into a
/// names-only <see cref="RegistryObservation"/>.
/// <para>
/// HARD RULE (invariant #5 — secrets kept apart): this reader only ever calls
/// <c>GetSubKeyNames()</c> and <c>GetValueNames()</c>. It NEVER calls <c>GetValue</c> —
/// a value classified as a secret must never be read into memory, even transiently. The
/// proof of presence is the value NAME alone.
/// </para>
/// V1 reads the current user's HKCU and the machine HKLM (64-bit view); other users'
/// offline NTUSER.DAT hives are a later increment (read-only via the offline-hive API,
/// never <c>RegLoadKey</c>, which would write).
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
                // Names only — never the values.
                return new RegistryObservation(Present: true, Error: null, key.GetValueNames(), Subkeys: []);
            }

            var subkeys = new List<SubkeyObservation>();
            foreach (var name in key.GetSubKeyNames())
            {
                subkeys.Add(ReadSubkeyNames(key, name));
            }

            return new RegistryObservation(Present: true, Error: null, ValueNames: [], subkeys);
        }
        catch (Exception ex) when (IsAccessFailure(ex))
        {
            return RegistryObservation.Unreadable(Reason(ex, target.Hive));
        }
    }

    private static SubkeyObservation ReadSubkeyNames(RegistryKey parent, string name)
    {
        try
        {
            using var sub = parent.OpenSubKey(name, writable: false);
            return new SubkeyObservation(name, sub?.GetValueNames() ?? []);
        }
        catch (Exception ex) when (IsAccessFailure(ex))
        {
            // One locked session: keep its name (counted), no value names (never guessed).
            return new SubkeyObservation(name, []);
        }
    }

    private static RegistryKey? OpenRoot(SecretHive hive) => hive switch
    {
        SecretHive.Hklm => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64),
        SecretHive.Hkcu => RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default),
        _ => null,
    };

    private static bool IsAccessFailure(Exception ex) =>
        // ArgumentException too: OpenSubKey throws it on a pathological key path (a
        // segment > 255 chars). A bad target degrades to Unreadable — never a crash.
        ex is SecurityException or UnauthorizedAccessException or IOException or ArgumentException;

    private static string Reason(Exception ex, SecretHive hive) =>
        ex is SecurityException or UnauthorizedAccessException
            ? (hive == SecretHive.Hklm ? "requires elevation" : "access denied")
            : ex.GetType().Name;
}
