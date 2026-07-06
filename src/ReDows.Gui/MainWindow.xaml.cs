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

    /// <summary>DWM attribute id: the system backdrop behind the window (Windows 11 22H2+).</summary>
    private const int DwmwaSystemBackdropType = 38;

    /// <summary>Backdrop kind: Mica (the soft, desktop-tinted material of Windows 11 apps).</summary>
    private const int BackdropMica = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref DwmMargins margins);

    [StructLayout(LayoutKind.Sequential)]
    private struct DwmMargins
    {
        public int Left, Right, Top, Bottom;
    }

    public MainWindow(ShellViewModel shell)
    {
        InitializeComponent();
        DataContext = shell;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Match the app's dark theme: ask DWM to paint the native title bar dark instead of the default
        // light bar. Best-effort: on a Windows build without this attribute the call is a harmless
        // no-op and the light bar simply stays. Never throws.
        var handle = new WindowInteropHelper(this).Handle;
        var enabled = 1;
        DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));

        // Mica backdrop (Windows 11 22H2+), still dependency-free: extend the frame, ask for Mica, and
        // ONLY then let it show through by clearing the window background. On any failure (older
        // Windows), nothing is changed and the solid dark background stays. A safe no-op.
        var margins = new DwmMargins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        if (DwmExtendFrameIntoClientArea(handle, ref margins) == 0)
        {
            var mica = BackdropMica;
            if (DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref mica, sizeof(int)) == 0)
            {
                Background = System.Windows.Media.Brushes.Transparent;
            }
        }
    }
}
