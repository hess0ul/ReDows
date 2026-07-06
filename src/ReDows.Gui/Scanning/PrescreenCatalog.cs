using System.Diagnostics;
using System.IO;
using ReDows.Core.Prescreening;

namespace ReDows.Gui.Scanning;

/// <summary>
/// Source of the fast-path file-prescreen classifier the Review screen uses to colour obvious entries
/// without an AI call. A seam: the real implementation reads prescreen/ shipped next to the app; a test
/// swaps a fake. Absence is not an error: no classifier just means every entry goes to the AI as before.
/// </summary>
public interface IPrescreenCatalog
{
    FilePrescreener? Load();
}

/// <summary>
/// Loads the shipped fast-path rules (prescreen/ next to the executable, falling back to the working
/// directory in dev: mirroring how the rules and modules are resolved). A malformed prescreen folder is
/// swallowed to <c>null</c> rather than crashing the window: the fast path is a pure optimisation, so a
/// broken file simply disables it (Review still works, every entry goes to the AI).
/// </summary>
public sealed class WindowsPrescreenCatalog : IPrescreenCatalog
{
    public FilePrescreener? Load()
    {
        try
        {
            return FilePrescreenerLoader.LoadDirectory(ResolvePrescreenDirectory());
        }
        catch (FilePrescreenerValidationException ex)
        {
            Debug.WriteLine($"Fast-path prescreen disabled (invalid): {ex.Message}");
            return null;
        }
    }

    private static string ResolvePrescreenDirectory()
    {
        var nextToExe = Path.Combine(AppContext.BaseDirectory, "prescreen");
        return Directory.Exists(nextToExe) ? nextToExe : "prescreen";
    }
}
