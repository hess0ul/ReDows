using Microsoft.Win32;
using ReDows.Core.Apps;

namespace ReDows.Providers.Windows.Apps;

/// <summary>
/// Win32 apps from the Add/Remove-Programs registry (the inventory backbone):
/// HKLM 64-bit view, HKLM 32-bit view, HKCU of the CURRENT user — enumerated via
/// RegistryView, never literal WOW6432Node paths. Read-only; whitelisted fields
/// only (UninstallString and license values are never copied — secrets).
/// </summary>
public static class ArpAppSource
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public sealed record ArpResult(IReadOnlyList<AppEntry> Entries, SourceAccounting Accounting);

    public static IReadOnlyList<ArpResult> Enumerate()
    {
        return
        [
            EnumerateHive("arp:hklm64", RegistryHive.LocalMachine, RegistryView.Registry64, "machine"),
            EnumerateHive("arp:hklm32", RegistryHive.LocalMachine, RegistryView.Registry32, "machine"),
            EnumerateHive("arp:hkcu", RegistryHive.CurrentUser, RegistryView.Default, "user"),
        ];
    }

    private static ArpResult EnumerateHive(string source, RegistryHive hive, RegistryView view, string scope)
    {
        var entries = new List<AppEntry>();
        long enumerated = 0, apps = 0, components = 0, updates = 0, errors = 0;

        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        using var uninstall = baseKey.OpenSubKey(UninstallKey);
        if (uninstall is null)
        {
            return new ArpResult(entries, new SourceAccounting(source, 0, 0, 0, 0, 0));
        }

        foreach (var subKeyName in uninstall.GetSubKeyNames())
        {
            enumerated++;
            try
            {
                using var key = uninstall.OpenSubKey(subKeyName);
                if (key is null)
                {
                    errors++;
                    continue;
                }

                var displayName = key.GetValue("DisplayName") as string;
                var systemComponent = key.GetValue("SystemComponent") as int?;
                var parentKeyName = key.GetValue("ParentKeyName") as string;
                var releaseType = key.GetValue("ReleaseType") as string;

                var (kind, note) = ArpEntryClassification.Classify(
                    displayName, systemComponent, parentKeyName, releaseType);
                switch (kind)
                {
                    case AppEntryKind.App: apps++; break;
                    case AppEntryKind.Component: components++; break;
                    case AppEntryKind.Update: updates++; break;
                }

                entries.Add(new AppEntry(
                    Key: $"{source}:{subKeyName}",
                    Source: source,
                    Kind: kind,
                    Name: displayName ?? subKeyName,
                    Version: key.GetValue("DisplayVersion") as string,
                    Publisher: key.GetValue("Publisher") as string,
                    InstallLocation: key.GetValue("InstallLocation") as string,
                    Scope: scope,
                    Note: note));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                errors++;
            }
        }

        return new ArpResult(entries, new SourceAccounting(source, enumerated, apps, components, updates, errors));
    }
}
