using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using ReDows.Gui.ViewModels;

namespace ReDows.Gui;

/// <summary>The window shell. Its DataContext is the ShellViewModel; nav + content are data-bound.</summary>
public partial class MainWindow : Window
{
    /// <summary>DWM attribute id: render the native title bar (the caption) dark (Windows 10 20H1+ / 11).</summary>
    private const int DwmwaUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    public MainWindow(ShellViewModel shell)
    {
        InitializeComponent();
        DataContext = shell;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Match the app's dark theme: ask DWM to paint the native title bar dark instead of the default
        // light bar. Best-effort — on a Windows build without this attribute the call is a harmless
        // no-op and the light bar simply stays. Never throws.
        var handle = new WindowInteropHelper(this).Handle;
        var enabled = 1;
        DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));
    }
}
