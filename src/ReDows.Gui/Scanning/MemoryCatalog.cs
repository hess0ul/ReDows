using System.Diagnostics;
using System.IO;
using ReDows.Core.Memory;

namespace ReDows.Gui.Scanning;

/// <summary>
/// Source of ReDows' folder memory — the known-folder/app notes the Review screen shows. A seam: the
/// real implementation reads memory/ shipped next to the app; a test swaps a fake. Absence is not an
/// error — no memory just means nothing is recognised and the tree colours only from the rules/AI.
/// </summary>
public interface IMemoryCatalog
{
    FolderMemory? Load();
}

/// <summary>
/// Loads the shipped folder memory (memory/ next to the executable, falling back to the working directory
/// in dev — mirroring how the rules, modules and triage are resolved). A malformed memory folder is
/// swallowed to <c>null</c> rather than crashing the window: the memory is an overlay, so a broken file
/// simply disables it (Review still works, colouring from the rules and the AI).
/// </summary>
public sealed class WindowsMemoryCatalog : IMemoryCatalog
{
    public FolderMemory? Load()
    {
        try
        {
            return MemoryLoader.LoadDirectory(ResolveMemoryDirectory());
        }
        catch (MemoryValidationException ex)
        {
            Debug.WriteLine($"Folder memory disabled (invalid): {ex.Message}");
            return null;
        }
    }

    private static string ResolveMemoryDirectory()
    {
        var nextToExe = Path.Combine(AppContext.BaseDirectory, "memory");
        return Directory.Exists(nextToExe) ? nextToExe : "memory";
    }
}
