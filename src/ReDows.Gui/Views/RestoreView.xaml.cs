using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ReDows.Gui.ViewModels;

namespace ReDows.Gui.Views;

/// <summary>
/// The Restore screen. Logic lives in RestoreViewModel; this opens the folder pickers and hands the
/// PasswordBox's value to the run — the vault password is passed transiently, never stored (invariant #5).
/// </summary>
public partial class RestoreView : UserControl
{
    public RestoreView() => InitializeComponent();

    private void BrowseBackup_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose the backup folder to restore" };
        if (dialog.ShowDialog() == true && DataContext is RestoreViewModel viewModel)
        {
            viewModel.BackupFolder = dialog.FolderName;
        }
    }

    private void BrowseTarget_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder to restore into" };
        if (dialog.ShowDialog() == true && DataContext is RestoreViewModel viewModel)
        {
            viewModel.TargetFolder = dialog.FolderName;
        }
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is RestoreViewModel viewModel)
        {
            await viewModel.RunAsync(VaultPassword.Password);
        }
    }
}
