namespace ReDows.Gui.ViewModels;

/// <summary>
/// The Settings screen. For now it holds the optional AI assistant's configuration (connection, model...),
/// the same instance Review uses, so setting it up here immediately enables Review's AI buttons. Kept as a
/// thin wrapper so more settings can join it later.
/// </summary>
public sealed class SettingsViewModel : ViewModelBase
{
    public SettingsViewModel(AiAssistantViewModel ai) => Ai = ai;

    /// <summary>The shared AI assistant, configured here and used in Review.</summary>
    public AiAssistantViewModel Ai { get; }
}
