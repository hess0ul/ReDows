using System.Collections.ObjectModel;
using ReDows.Gui.Reviewing;

namespace ReDows.Gui.ViewModels;

/// <summary>
/// One review-pile location that belongs to an installed app (a head folder the wizard would otherwise
/// make you walk to). It carries the rule-based keep/drop <see cref="SuggestKey"/> (a starting point,
/// never a decision) and its live <see cref="IsDropped"/> state, mirrored from the review trash so the
/// panel and the wizard stay in step. Dropping is restorable, like everything else under review.
/// </summary>
public sealed class AppReviewLocationViewModel : ViewModelBase
{
    private bool _isDropped;
    private string _suggestKey;
    private string _suggestReason;

    public AppReviewLocationViewModel(EntryRow root, string suggestKey, string suggestReason)
    {
        Root = root;
        _suggestKey = suggestKey;
        _suggestReason = suggestReason;
    }

    public EntryRow Root { get; }

    public string FullPath => Root.FullPath;

    public string Name => Root.Name;

    /// <summary>The suggested colour: "keep" (blue) / "maybe" (pink) / "drop" (purple). The rules set it
    /// first; the optional AI proposal can refine it, and the dot recolours live.</summary>
    public string SuggestKey
    {
        get => _suggestKey;
        set { Set(ref _suggestKey, value); Raise(nameof(SuggestDrop)); }
    }

    /// <summary>Why this colour was suggested (a rule reason or the AI's own explanation); the row's tooltip.</summary>
    public string SuggestReason
    {
        get => _suggestReason;
        set => Set(ref _suggestReason, value);
    }

    /// <summary>True when the suggestion is "safe to drop" (targeted by "Drop suggested").</summary>
    public bool SuggestDrop => SuggestKey == "drop";

    /// <summary>Whether this location is currently in the trash (won't be backed up). Set by the view-model.</summary>
    public bool IsDropped
    {
        get => _isDropped;
        set => Set(ref _isDropped, value);
    }
}

/// <summary>
/// One installed app and the review-pile head folders that are its data, so the user can keep or drop
/// the whole app's data as a block BEFORE walking the rest of the review folder by folder. Read-only:
/// membership is a name match against the app inventory; the actions only ever move folders in or out
/// of the review trash.
/// </summary>
public sealed class AppReviewGroupViewModel : ViewModelBase
{
    private bool _isExpanded;

    public AppReviewGroupViewModel(string appName, IReadOnlyList<AppReviewLocationViewModel> locations)
    {
        AppName = appName;
        Locations = new ObservableCollection<AppReviewLocationViewModel>(locations);
    }

    public string AppName { get; }

    public ObservableCollection<AppReviewLocationViewModel> Locations { get; }

    public int LocationCount => Locations.Count;

    public string Header => $"{AppName}  ·  {LocationCount} location(s)";

    public bool IsExpanded
    {
        get => _isExpanded;
        set => Set(ref _isExpanded, value);
    }
}
