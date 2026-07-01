using System.Collections.ObjectModel;
using ReDows.Gui.Navigation;
using ReDows.Gui.Reviewing;

namespace ReDows.Gui.ViewModels;

public enum ReviewSort
{
    Size,
    Name,
    Type,
}

/// <summary>
/// The review wizard, trash model: everything under review is KEPT by default (safe — nothing lost
/// by forgetting). You walk the REVIEW folders one at a time (Folder X of N, Previous/Next), drill in
/// (Open / Up), and DROP the junk — a dropped item leaves the list and goes to the trash, which you can
/// open to restore. Folders are read on demand (read-only). The kept set (everything minus the trash)
/// will feed the backup next.
/// </summary>
public sealed class ReviewViewModel : ViewModelBase
{
    private readonly IFolderBrowser _browser;
    private IReadOnlyList<EntryRow> _roots = [];
    private int _folderIndex = -1;
    private readonly List<(string Path, long Bytes)> _trail = [];
    private IReadOnlyList<EntryRow> _current = [];
    private long _totalReviewBytes;
    private CancellationTokenSource? _cancellation;
    private ReviewSort _sort = ReviewSort.Size;

    private bool _isLoading;
    private bool _isTrashOpen;
    private string? _error;
    private string _location = "No scan yet — run a scan first, then come here.";
    private string _stepText = "";
    private string _folderNote = "";

    public ReviewViewModel(IFolderBrowser browser)
    {
        _browser = browser;
        OpenCommand = new RelayCommand(item => { if (item is EntryRow entry) _ = OpenAsync(entry); }, _ => !IsLoading);
        DropCommand = new RelayCommand(item => { if (item is EntryRow entry) DropEntry(entry); }, _ => !IsLoading);
        RestoreCommand = new RelayCommand(item => { if (item is TrashRow trashed) _ = RestoreAsync(trashed); });
        UpCommand = new RelayCommand(_ => _ = UpAsync(), _ => !AtFolderRoot && !IsLoading);
        CancelCommand = new RelayCommand(_ => _cancellation?.Cancel(), _ => IsLoading);
        NextCommand = new RelayCommand(_ => _ = NextAsync(), _ => HasNext && !IsLoading);
        PreviousCommand = new RelayCommand(_ => _ = PreviousAsync(), _ => HasPrevious && !IsLoading);
        DropFolderCommand = new RelayCommand(_ => _ = DropCurrentFolderAsync(), _ => HasFolder && !IsLoading);
        ToggleTrashCommand = new RelayCommand(_ => IsTrashOpen = !IsTrashOpen);
        SortBySizeCommand = new RelayCommand(_ => SetSort(ReviewSort.Size));
        SortByNameCommand = new RelayCommand(_ => SetSort(ReviewSort.Name));
        SortByTypeCommand = new RelayCommand(_ => SetSort(ReviewSort.Type));
    }

    public DropSelection Trash { get; } = new();

    public ObservableCollection<EntryRow> Entries { get; } = [];

    public ObservableCollection<TrashRow> TrashItems { get; } = [];

    public RelayCommand OpenCommand { get; }

    public RelayCommand DropCommand { get; }

    public RelayCommand RestoreCommand { get; }

    public RelayCommand UpCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand NextCommand { get; }

    public RelayCommand PreviousCommand { get; }

    public RelayCommand DropFolderCommand { get; }

    public RelayCommand ToggleTrashCommand { get; }

    public RelayCommand SortBySizeCommand { get; }

    public RelayCommand SortByNameCommand { get; }

    public RelayCommand SortByTypeCommand { get; }

    public bool HasRoots => _roots.Count > 0;

    public bool HasFolder => _folderIndex >= 0 && _folderIndex < _roots.Count;

    public bool HasNext => _folderIndex >= 0 && _folderIndex < _roots.Count - 1;

    /// <summary>On a folder with no next one — the wizard's end, where "Next" becomes "Back up →".</summary>
    public bool OnLastFolder => HasFolder && !HasNext;

    public bool HasPrevious => _folderIndex > 0;

    public bool AtFolderRoot => _trail.Count <= 1;

    public string Location
    {
        get => _location;
        private set => Set(ref _location, value);
    }

    public string StepText
    {
        get => _stepText;
        private set => Set(ref _stepText, value);
    }

    public string FolderNote
    {
        get => _folderNote;
        private set => Set(ref _folderNote, value);
    }

    public bool IsTrashOpen
    {
        get => _isTrashOpen;
        set => Set(ref _isTrashOpen, value);
    }

    public string TrashButtonText => $"🗑 Trash ({Trash.DroppedCount:N0})";

    public string KeptSummary => Trash.DroppedCount == 0
        ? $"Keeping everything under review (≈ {Format.Bytes(_totalReviewBytes)}). Drop what you don't need."
        : $"Trash: {Trash.DroppedCount:N0} item(s) · {Format.Bytes(Trash.DroppedBytes)} — keeping ≈ {Format.Bytes(Math.Max(0, _totalReviewBytes - Trash.DroppedBytes))}";

    public bool IsLoading
    {
        get => _isLoading;
        private set { Set(ref _isLoading, value); RaiseNav(); }
    }

    public string? Error
    {
        get => _error;
        private set => Set(ref _error, value);
    }

    /// <summary>Feed the biggest REVIEW folders from the latest scan and start at the first one.</summary>
    public void SetRoots(IReadOnlyList<EntryRow> roots)
    {
        _roots = roots;
        _folderIndex = -1;
        _trail.Clear();
        _totalReviewBytes = roots.Sum(r => r.Bytes);
        Error = null;
        IsTrashOpen = false;
        RefreshTrash();
        Raise(nameof(HasRoots));

        if (roots.Count == 0)
        {
            _current = [];
            Entries.Clear();
            StepText = "";
            FolderNote = "";
            Location = "No scan yet — run a scan first, then come here.";
            RaiseSummary();
            RaiseNav();
            return;
        }

        _ = GoToFolderAsync(0);
    }

    public async Task GoToFolderAsync(int index)
    {
        if (index < 0 || index >= _roots.Count)
        {
            return;
        }

        _folderIndex = index;
        _trail.Clear();
        _trail.Add((_roots[index].FullPath, _roots[index].Bytes));
        StepText = $"Folder {index + 1} of {_roots.Count}";
        await LoadCurrentAsync();
    }

    public Task NextAsync() => HasNext ? GoToFolderAsync(_folderIndex + 1) : Task.CompletedTask;

    public Task PreviousAsync() => HasPrevious ? GoToFolderAsync(_folderIndex - 1) : Task.CompletedTask;

    public async Task OpenAsync(EntryRow entry)
    {
        if (!entry.IsDirectory || IsLoading)
        {
            return;
        }

        _trail.Add((entry.FullPath, entry.Bytes));
        await LoadCurrentAsync();
    }

    public async Task UpAsync()
    {
        if (AtFolderRoot || IsLoading)
        {
            return;
        }

        _trail.RemoveAt(_trail.Count - 1);
        await LoadCurrentAsync();
    }

    public void DropEntry(EntryRow entry)
    {
        Trash.Drop(entry.FullPath, entry.Bytes);
        Entries.Remove(entry);
        RefreshTrash();
        RaiseSummary();
    }

    public async Task DropCurrentFolderAsync()
    {
        if (!HasFolder)
        {
            return;
        }

        var (path, bytes) = _trail[^1];
        Trash.Drop(path, bytes);
        RefreshTrash();

        if (!AtFolderRoot)
        {
            _trail.RemoveAt(_trail.Count - 1); // go back to the parent, where this folder is now gone
            await LoadCurrentAsync();
        }
        else
        {
            Entries.Clear();
            _current = [];
            FolderNote = "This folder is in the trash — click Next ▶, or open the trash to restore it.";
            RaiseSummary();
            RaiseNav();
        }
    }

    public async Task RestoreAsync(TrashRow trashed)
    {
        Trash.Restore(trashed.FullPath);
        RefreshTrash();
        RaiseSummary();
        if (HasFolder)
        {
            await LoadCurrentAsync(); // bring the item back into view if it belongs here
        }
    }

    private async Task LoadCurrentAsync()
    {
        var path = _trail[^1].Path;
        Location = path;
        Error = null;
        FolderNote = "";
        IsLoading = true;
        _cancellation = new CancellationTokenSource();
        try
        {
            _current = await _browser.ListAsync(path, _cancellation.Token);
            ApplySort();
            if (Entries.Count == 0)
            {
                FolderNote = Trash.IsDropped(path) ? "This folder is in the trash." : "Nothing left here.";
            }
        }
        catch (OperationCanceledException)
        {
            if (!AtFolderRoot)
            {
                _trail.RemoveAt(_trail.Count - 1);
                Location = _trail[^1].Path;
            }
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            _current = [];
            Entries.Clear();
        }
        finally
        {
            IsLoading = false;
            _cancellation?.Dispose();
            _cancellation = null;
            RaiseNav();
        }
    }

    private void SetSort(ReviewSort sort)
    {
        _sort = sort;
        ApplySort();
    }

    private void ApplySort()
    {
        var visible = _current.Where(e => !Trash.IsDropped(e.FullPath));
        IEnumerable<EntryRow> ordered = _sort switch
        {
            ReviewSort.Name => visible.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase),
            ReviewSort.Type => visible.OrderBy(e => e.Kind, StringComparer.OrdinalIgnoreCase).ThenByDescending(e => e.Bytes),
            _ => visible.OrderByDescending(e => e.Bytes),
        };

        Entries.Clear();
        foreach (var entry in ordered)
        {
            Entries.Add(entry);
        }
    }

    private void RefreshTrash()
    {
        TrashItems.Clear();
        foreach (var item in Trash.Items.OrderByDescending(i => i.Value))
        {
            TrashItems.Add(TrashRow.From(item.Key, item.Value));
        }

        Raise(nameof(TrashButtonText));
    }

    private void RaiseSummary()
    {
        Raise(nameof(KeptSummary));
        Raise(nameof(TrashButtonText));
    }

    private void RaiseNav()
    {
        Raise(nameof(HasFolder));
        Raise(nameof(HasNext));
        Raise(nameof(OnLastFolder));
        Raise(nameof(HasPrevious));
        Raise(nameof(AtFolderRoot));
        UpCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        NextCommand.RaiseCanExecuteChanged();
        PreviousCommand.RaiseCanExecuteChanged();
        DropFolderCommand.RaiseCanExecuteChanged();
        OpenCommand.RaiseCanExecuteChanged();
        DropCommand.RaiseCanExecuteChanged();
    }
}
