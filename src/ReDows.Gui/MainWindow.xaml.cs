using System.Windows;
using ReDows.Gui.ViewModels;

namespace ReDows.Gui;

/// <summary>The window shell. Its DataContext is the ShellViewModel; nav + content are data-bound.</summary>
public partial class MainWindow : Window
{
    public MainWindow(ShellViewModel shell)
    {
        InitializeComponent();
        DataContext = shell;
    }
}
