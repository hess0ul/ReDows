using System.Windows.Controls;
using ReDows.Gui.ViewModels;

namespace ReDows.Gui.Views;

/// <summary>
/// The review explorer. All logic lives in ReviewViewModel; clicks go through its commands. The one
/// piece of code-behind hands the AI PasswordBox's value to the view-model for THIS session only —
/// the key stays in memory and is never persisted or logged (invariant #5).
/// </summary>
public partial class ReviewView : UserControl
{
    public ReviewView() => InitializeComponent();

    private void AiApiKey_PasswordChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is ReviewViewModel viewModel && sender is PasswordBox box)
        {
            viewModel.Ai?.SetApiKey(box.Password);
        }
    }
}
