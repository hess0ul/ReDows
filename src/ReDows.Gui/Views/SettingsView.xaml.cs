using System.Windows.Controls;
using ReDows.Gui.ViewModels;

namespace ReDows.Gui.Views;

/// <summary>
/// The Settings screen. All logic lives in SettingsViewModel / the shared AiAssistantViewModel. The one
/// piece of code-behind hands the AI PasswordBox's value to the assistant for THIS session only. The key
/// stays in memory and is never persisted or logged (invariant #5).
/// </summary>
public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private void AiApiKey_PasswordChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel && sender is PasswordBox box)
        {
            viewModel.Ai.SetApiKey(box.Password);
        }
    }
}
