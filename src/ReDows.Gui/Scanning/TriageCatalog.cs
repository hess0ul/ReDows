using System.Diagnostics;
using System.IO;
using ReDows.Core.Triage;

namespace ReDows.Gui.Scanning;

/// <summary>
/// Source of the fast-path file-triage classifier the Review screen uses to colour obvious entries
/// without an AI call. A seam: the real implementation reads triage/ shipped next to the app; a test
/// swaps a fake. Absence is not an error — no classifier just means every entry goes to the AI as before.
/// </summary>
public interface ITriageCatalog
{
    FileTriage? Load();
}

/// <summary>
/// Loads the shipped fast-path rules (triage/ next to the executable, falling back to the working
/// directory in dev — mirroring how the rules and modules are resolved). A malformed triage folder is
/// swallowed to <c>null</c> rather than crashing the window: the fast path is a pure optimisation, so a
/// broken file simply disables it (Review still works, every entry goes to the AI).
/// </summary>
public sealed class WindowsTriageCatalog : ITriageCatalog
{
    public FileTriage? Load()
    {
        try
        {
            return FileTriageLoader.LoadDirectory(ResolveTriageDirectory());
        }
        catch (FileTriageValidationException ex)
        {
            Debug.WriteLine($"Fast-path triage disabled (invalid): {ex.Message}");
            return null;
        }
    }

    private static string ResolveTriageDirectory()
    {
        var nextToExe = Path.Combine(AppContext.BaseDirectory, "triage");
        return Directory.Exists(nextToExe) ? nextToExe : "triage";
    }
}
