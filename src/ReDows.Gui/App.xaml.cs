using System.Windows;
using ReDows.Gui.Backup;
using ReDows.Gui.Context;
using ReDows.Gui.Restore;
using ReDows.Gui.Reviewing;
using ReDows.Gui.Scanning;
using ReDows.Gui.ViewModels;

namespace ReDows.Gui;

/// <summary>
/// Application entry point. The shell is built here with the real, read-only sources (context +
/// scan) — the same seams a test replaces with fakes — then handed to the window.
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var shell = new ShellViewModel(new WindowsContextSource(), new WindowsScanRunner(), new WindowsFolderBrowser(), new WindowsModuleCatalog(), new WindowsBackupRunner(), new WindowsRestoreRunner());
        var window = new MainWindow(shell);
        shell.Initialize();
        window.Show();
    }
}
