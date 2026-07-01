using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ReDows.Gui.ViewModels;

namespace ReDows.Gui.Views;

/// <summary>The Scan screen. All logic is in ScanViewModel; this only opens the folder picker.</summary>
public partial class ScanView : UserControl
{
    public ScanView() => InitializeComponent();

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder to scan" };
        if (dialog.ShowDialog() == true && DataContext is ScanViewModel viewModel)
        {
            viewModel.FolderPath = dialog.FolderName;
        }
    }
}
