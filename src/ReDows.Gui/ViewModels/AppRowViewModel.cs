using ReDows.Core.Apps;

namespace ReDows.Gui.ViewModels;

/// <summary>
/// One installed app in the Apps list, with a tick for "reinstall this after the reset" (on by default).
/// It carries the underlying <see cref="AppEntry"/> so the chosen rows can be handed to the InDows profile.
/// </summary>
public sealed class AppRowViewModel(AppEntry entry) : ViewModelBase
{
    private bool _isSelected = true;

    public AppEntry Entry { get; } = entry;

    public string Name => string.IsNullOrWhiteSpace(Entry.Name) ? Entry.Key : Entry.Name!;

    public string Source => Entry.Source;

    /// <summary>Whether the app can be reinstalled automatically — has a reinstall id (winget / steam / choco…).</summary>
    public bool IsAuto => Entry.Reinstall is not null;

    /// <summary>A short badge: how it would reinstall ("auto · winget") or that it is manual.</summary>
    public string ReinstallBadge => Entry.Reinstall is { } hint ? $"auto · {hint.Kind}" : "manual";

    /// <summary>Reinstall this app after the reset (feeds the InDows profile). On by default (forget-nothing).</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }
}
