using System.Diagnostics;
using System.IO;
using ReDows.Core.Modules;

namespace ReDows.Gui.Scanning;

/// <summary>
/// Source of the category-module definitions the Scan screen offers (games, media…). A seam: the
/// real implementation reads modules/ shipped next to the app; a test swaps a fake so the scan
/// view-model can be exercised with a known set. Absence is not an error — no modules just means the
/// scan behaves as if every category were left on "review".
/// </summary>
public interface IModuleCatalog
{
    IReadOnlyList<ModuleDefinition> Load();
}

/// <summary>
/// Loads the shipped modules (modules/ next to the executable, falling back to the working directory
/// in dev — mirroring how the rules are resolved). A malformed module folder is swallowed to an empty
/// list rather than crashing the window: category modules are an optional convenience, so a broken one
/// simply disables the feature (the scan still runs, everything stays on "review").
/// </summary>
public sealed class WindowsModuleCatalog : IModuleCatalog
{
    public IReadOnlyList<ModuleDefinition> Load()
    {
        try
        {
            return ModuleLoader.LoadDirectory(ResolveModulesDirectory());
        }
        catch (ModuleValidationException ex)
        {
            Debug.WriteLine($"Category modules disabled (invalid): {ex.Message}");
            return [];
        }
    }

    private static string ResolveModulesDirectory()
    {
        var nextToExe = Path.Combine(AppContext.BaseDirectory, "modules");
        return Directory.Exists(nextToExe) ? nextToExe : "modules";
    }
}
