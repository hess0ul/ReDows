using ReDows.Providers.Windows;

namespace ReDows.Gui.Context;

/// <summary>
/// Supplies the machine's read-only context to the UI. A seam: the real implementation reads
/// this PC; a test swaps in a fake, so view-models are exercised without touching the machine.
/// </summary>
public interface IContextSource
{
    WindowsContext Load();
}

/// <summary>Reads the real machine's context — READ-ONLY — via the same provider the CLI uses.</summary>
public sealed class WindowsContextSource : IContextSource
{
    public WindowsContext Load() => WindowsScanContextProvider.Build();
}
