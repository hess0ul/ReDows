using ReDows.Core.Apps;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace ReDows.Providers.Windows.Apps;

/// <summary>
/// Store/MSIX packages of the CURRENT user via the supported WinRT API
/// (PackageManager.FindPackagesForUser("") — no elevation needed). Framework,
/// resource and OS-signed packages go to the visible components bucket; the
/// stable identity is the PackageFamilyName. Per-package property reads are
/// guarded: staged or damaged packages throw on individual properties (CsWinRT
/// quirk) and must become counted errors, never a crash.
/// </summary>
public static class MsixAppSource
{
    public const string SourceName = "msix";

    public sealed record MsixResult(
        IReadOnlyList<AppEntry> Entries,
        SourceAccounting Accounting,
        InventoryDegradation? Degradation);

    public static MsixResult Enumerate()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            // Older OS than our WinRT projection floor: counted, never silent.
            return new MsixResult(
                [],
                new SourceAccounting(SourceName, 0, 0, 0, 0, 0),
                new InventoryDegradation(SourceName, "this machine",
                    "Windows build < 19041 — MSIX enumeration unavailable, Store apps not inventoried"));
        }

        var entries = new List<AppEntry>();
        long enumerated = 0, apps = 0, components = 0, errors = 0;
        InventoryDegradation? degradation = null;

        // Source-level guard: PackageManager activation or the lazy enumeration
        // itself can fail (AppX service unavailable, COM error mid-stream). The
        // partial inventory is kept and the failure becomes a counted
        // degradation — never a crash that loses every other source.
        try
        {
            var packageManager = new PackageManager();
            foreach (var package in packageManager.FindPackagesForUser(string.Empty))
            {
                enumerated++;
                try
                {
                    var id = package.Id;
                    var isComponent = package.IsFramework
                        || package.IsResourcePackage
                        || package.SignatureKind == PackageSignatureKind.System;

                    string? displayName;
                    try
                    {
                        displayName = package.DisplayName;
                        if (string.IsNullOrWhiteSpace(displayName) || displayName.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
                        {
                            displayName = id.Name;
                        }
                    }
                    catch
                    {
                        displayName = id.Name;
                    }

                    string? installLocation;
                    try
                    {
                        installLocation = package.InstalledPath;
                    }
                    catch
                    {
                        installLocation = null; // staged/damaged package: location unavailable
                    }

                    var version = id.Version;
                    entries.Add(new AppEntry(
                        Key: $"{SourceName}:{id.FamilyName}",
                        Source: SourceName,
                        Kind: isComponent ? AppEntryKind.Component : AppEntryKind.App,
                        Name: displayName,
                        Version: $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}",
                        Publisher: SafePublisherDisplayName(package) ?? id.Publisher,
                        InstallLocation: installLocation,
                        Scope: "user",
                        Reinstall: package.SignatureKind == PackageSignatureKind.Store
                            ? new ReinstallHint("msstore", id.FamilyName, ReinstallConfidence.Exact)
                            : null,
                        Note: package.SignatureKind switch
                        {
                            PackageSignatureKind.Store => null,
                            PackageSignatureKind.System => "OS-signed package",
                            PackageSignatureKind.Developer or PackageSignatureKind.None => "sideloaded — manual reinstall",
                            PackageSignatureKind.Enterprise => "enterprise-signed — redeployed by the organization",
                            _ => null,
                        }));

                    // Counters move only once the entry exists: the accounting
                    // equation (enumerated = apps + components + errors) must hold.
                    if (isComponent)
                    {
                        components++;
                    }
                    else
                    {
                        apps++;
                    }
                }
                catch (Exception)
                {
                    errors++;
                }
            }
        }
        catch (Exception ex)
        {
            // The failed iteration was counted in 'enumerated' but produced
            // neither entry nor bucket: align the equation as a counted error.
            errors = enumerated - apps - components;
            degradation = new InventoryDegradation(SourceName, "this machine",
                $"MSIX enumeration failed ({ex.GetType().Name}: {ex.Message}) — Store inventory incomplete ({apps + components} package(s) kept)");
        }

        return new MsixResult(
            entries,
            new SourceAccounting(SourceName, enumerated, apps, components, Updates: 0, errors),
            degradation);
    }

    private static string? SafePublisherDisplayName(Package package)
    {
        try
        {
            var publisher = package.PublisherDisplayName;
            return string.IsNullOrWhiteSpace(publisher) ? null : publisher;
        }
        catch
        {
            return null;
        }
    }
}
