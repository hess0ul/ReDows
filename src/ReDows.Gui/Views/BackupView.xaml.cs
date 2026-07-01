using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ReDows.Gui.ViewModels;

namespace ReDows.Gui.Views;

/// <summary>
/// The Backup screen. Logic lives in BackupViewModel; this only opens the destination picker and hands
/// the PasswordBox's value to the run — the password is passed transiently, never stored on the
/// view-model or logged (invariant #5).
/// </summary>
public partial class BackupView : UserControl
{
    public BackupView() => InitializeComponent();

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose where to save the backup" };
        if (dialog.ShowDialog() == true && DataContext is BackupViewModel viewModel)
        {
            viewModel.Destination = dialog.FolderName;
        }
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is BackupViewModel viewModel)
        {
            // Read the password only at run time, only if the vault is enabled; then let it go.
            var password = viewModel.UseVault ? VaultPassword.Password : null;
            await viewModel.RunAsync(password);
        }
    }
}
