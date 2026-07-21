using System.Windows.Controls;

namespace ReDows.Gui.Views;

/// <summary>
/// The review explorer. All logic lives in ReviewViewModel; clicks go through its commands. The AI
/// assistant is configured on the Settings screen now, so there is no code-behind here.
/// </summary>
public partial class ReviewView : UserControl
{
    public ReviewView() => InitializeComponent();
}
