using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ReDows.Gui.ViewModels;

namespace ReDows.Gui.Views;

/// <summary>The Apps screen. Logic lives in AppsViewModel; this only opens the folder picker for the export.</summary>
public partial class AppsView : UserControl
{
    public AppsView() => InitializeComponent();

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose where to write the InDows profile" };
        if (dialog.ShowDialog() == true && DataContext is AppsViewModel viewModel)
        {
            await viewModel.ExportAsync(dialog.FolderName);
        }
    }
}
