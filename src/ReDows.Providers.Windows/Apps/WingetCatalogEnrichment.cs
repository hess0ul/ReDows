using Microsoft.Management.Deployment;
using ReDows.Core.Apps;

namespace ReDows.Providers.Windows.Apps;

/// <summary>
/// Row-level winget correlation through the official COM API (the engine behind
/// `winget list`): the composite of the installed catalog plus the configured
/// sources yields, per installed package, the matched winget id AND the exact
/// keys (ARP product codes, MSIX family names) we attach with. Same opt-in
/// deviation class as the export path (runs the winget server, may touch the
/// network); on any failure the caller falls back to `winget export`.
/// </summary>
public static class WingetCatalogEnrichment
{
    public sealed record Result(
        IReadOnlyList<WingetInstalledMatch> Matches,
        InventoryDegradation? Degradation,
        string? Note);

    public static Result Run()
    {
        var stage = "activation";
        try
        {
            // The ComInterop package ships winget's engine as an "undocked"
            // in-process native library + CsWinRT projection: plain construction
            // activates reg-free against the DLL deployed next to the exe.
            var packageManager = new PackageManager();

            stage = "options";
            var compositeOptions = new CreateCompositePackageCatalogOptions();

            // The predefined catalogs cover the correlation need (the in-proc
            // engine does not expose the configured-sources enumeration).
            var failedCatalogs = new List<string>();
            foreach (var predefined in (PredefinedPackageCatalog[])
                [PredefinedPackageCatalog.OpenWindowsCatalog, PredefinedPackageCatalog.MicrosoftStore])
            {
                stage = $"GetPredefinedPackageCatalog({predefined})";
                try
                {
                    compositeOptions.Catalogs.Add(packageManager.GetPredefinedPackageCatalog(predefined));
                }
                catch (Exception)
                {
                    // A missing predefined source degrades correlation quality,
                    // not correctness: unmatched apps stay reinstall:manual —
                    // but the note must say WHY matching was poor (§0-5).
                    failedCatalogs.Add(predefined.ToString());
                }
            }

            // LocalCatalogs: enumerate INSTALLED packages, remote sources used
            // only to correlate them — exactly `winget list`.
            compositeOptions.CompositeSearchBehavior = CompositeSearchBehavior.LocalCatalogs;

            stage = "Connect";
            var connect = packageManager.CreateCompositePackageCatalog(compositeOptions).Connect();
            if (connect.Status != ConnectResultStatus.Ok)
            {
                return Failure($"catalog connect failed ({connect.Status})");
            }

            stage = "FindPackages";
            var find = connect.PackageCatalog.FindPackages(new FindPackagesOptions());
            if (find.Status != FindPackagesResultStatus.Ok)
            {
                return Failure($"FindPackages failed ({find.Status})");
            }

            stage = "Matches.Count";
            var matchList = find.Matches;
            var count = matchList.Count;

            var matches = new List<WingetInstalledMatch>();
            for (var i = 0; i < count; i++)
            {
                stage = $"match[{i}].CatalogPackage";
                var package = matchList[i].CatalogPackage;

                stage = $"match[{i}].InstalledVersion";
                var installed = package.InstalledVersion;
                if (installed is null)
                {
                    continue;
                }

                stage = $"match[{i}].Id";
                var id = package.Id;

                stage = $"match[{i}].DefaultInstallVersion";
                var catalogName = package.DefaultInstallVersion?.PackageCatalog?.Info?.Name;

                stage = $"match[{i}].ProductCodes";
                string[] productCodes = [.. installed.ProductCodes];

                stage = $"match[{i}].PackageFamilyNames";
                string[] familyNames = [.. installed.PackageFamilyNames];

                matches.Add(new WingetInstalledMatch(id, catalogName, productCodes, familyNames));
            }

            var correlated = matches.Count(m => m.CatalogName is not null);
            var failedSuffix = failedCatalogs.Count == 0
                ? ""
                : $" (predefined catalog(s) unavailable: {string.Join(", ", failedCatalogs)})";
            return new Result(matches, null,
                $"winget correlation engine (COM): {correlated} of {matches.Count} installed package(s) matched to a source{failedSuffix}");
        }
        catch (Exception ex)
        {
            // COM activation can fail for many environmental reasons (winget
            // absent, server not activatable, projection mismatch): counted,
            // and the export fallback takes over.
            return Failure($"{ex.GetType().Name} at '{stage}': {ex.Message}");
        }

        static Result Failure(string reason) => new([], new InventoryDegradation(
            "winget", "COM correlation", $"{reason} — falling back to `winget export` corroboration"), null);
    }

}
