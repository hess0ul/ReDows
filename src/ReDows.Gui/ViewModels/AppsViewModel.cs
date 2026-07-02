using System.Collections.ObjectModel;
using System.ComponentModel;
using ReDows.Gui.Apps;
using ReDows.Gui.Navigation;

namespace ReDows.Gui.ViewModels;

/// <summary>
/// The Apps screen's brain. It loads this PC's installed apps off the UI thread (winget-enriched, so as
/// many as possible can reinstall automatically), lists them with a tick each (all on by default), and
/// writes the chosen ones plus this PC's Windows settings into a FULL InDows profile (apps + settings +
/// README) — the complete ReDows → InDows hand-off. Both operations run in the background with progress and
/// Cancel. All state is plain and testable off a fake <see cref="IAppsRunner"/>.
/// </summary>
public sealed class AppsViewModel : ViewModelBase
{
    private readonly IAppsRunner _runner;
    private CancellationTokenSource? _cancellation;

    private bool _isBusy;
    private bool _loaded;
    private bool _suppressSelectionEvents; // while applying a bulk toggle or a restored selection
    private IReadOnlyList<string>? _pendingDeselect; // a resumed selection to apply once the inventory loads
    private string _progressText = "";
    private string _summary = "";
    private string? _error;
    private string? _exportResult;

    public AppsViewModel(IAppsRunner runner)
    {
        _runner = runner;
        SelectAllCommand = new RelayCommand(_ => SetAll(true));
        SelectNoneCommand = new RelayCommand(_ => SetAll(false));
        CancelCommand = new RelayCommand(_ => Cancel(), _ => IsBusy);
    }

    /// <summary>The installed apps, each with a tick for "reinstall after the reset".</summary>
    public ObservableCollection<AppRowViewModel> Apps { get; } = [];

    /// <summary>Raised when the user changes which apps are ticked — the shell persists the choice on this signal.</summary>
    public event Action? SelectionChanged;

    /// <summary>The keys of the apps the user unticked (a deny-list — everything else reinstalls by default).</summary>
    public IReadOnlyList<string> DeselectedKeys =>
        Apps.Where(app => !app.IsSelected).Select(app => app.Entry.Key).ToList();

    /// <summary>
    /// Re-apply a saved session's app choices: untick exactly the given keys, everything else stays ticked
    /// (so an app installed since the last session is kept by default). Applied now if loaded, otherwise the
    /// moment the inventory finishes loading. Does not raise <see cref="SelectionChanged"/> — resuming, not deciding.
    /// </summary>
    public void RestoreSelection(IReadOnlyList<string> deselectedKeys)
    {
        _pendingDeselect = deselectedKeys;
        if (_loaded)
        {
            ApplyPendingDeselect();
        }
    }

    public RelayCommand SelectAllCommand { get; }

    public RelayCommand SelectNoneCommand { get; }

    public RelayCommand CancelCommand { get; }

    /// <summary>Loading the inventory or writing the profile — either way, busy (progress + Cancel shown).</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set { Set(ref _isBusy, value); Raise(nameof(CanExport)); CancelCommand.RaiseCanExecuteChanged(); }
    }

    public string ProgressText
    {
        get => _progressText;
        private set => Set(ref _progressText, value);
    }

    /// <summary>How many apps, how many reinstall automatically vs manually.</summary>
    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    public string? Error
    {
        get => _error;
        private set => Set(ref _error, value);
    }

    public string? ExportResult
    {
        get => _exportResult;
        private set => Set(ref _exportResult, value);
    }

    /// <summary>Ready to export: the inventory has loaded and no operation is in flight.</summary>
    public bool CanExport => !IsBusy && _loaded && Apps.Count > 0;

    /// <summary>Load the inventory once (on first visit). Winget enrichment is on so the profile is useful.</summary>
    public async Task LoadAsync()
    {
        if (IsBusy || _loaded)
        {
            return;
        }

        Error = null;
        ExportResult = null;
        ProgressText = "Starting…";
        IsBusy = true;
        _cancellation = new CancellationTokenSource();
        var progress = new Progress<string>(text => ProgressText = text);
        try
        {
            var result = await _runner.LoadAsync(enrichWithWinget: true, progress, _cancellation.Token);
            foreach (var old in Apps)
            {
                old.PropertyChanged -= OnRowChanged;
            }

            Apps.Clear();
            foreach (var entry in result.Apps)
            {
                var row = new AppRowViewModel(entry);
                row.PropertyChanged += OnRowChanged;
                Apps.Add(row);
            }

            Summary = result.Summary;
            ProgressText = "";
            _loaded = true;
            ApplyPendingDeselect(); // re-apply a selection restored from a session before the inventory loaded
            Raise(nameof(CanExport));
        }
        catch (OperationCanceledException)
        {
            ProgressText = "Cancelled.";
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            ProgressText = "";
        }
        finally
        {
            IsBusy = false;
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }

    /// <summary>Write the FULL InDows profile (ticked apps + Windows settings + README) into a folder.</summary>
    public async Task ExportAsync(string folder)
    {
        if (IsBusy || !_loaded)
        {
            return;
        }

        Error = null;
        ExportResult = null;
        ProgressText = "Starting export…";
        IsBusy = true;
        _cancellation = new CancellationTokenSource();
        var progress = new Progress<string>(text => ProgressText = text);
        try
        {
            var selected = Apps.Where(app => app.IsSelected).Select(app => app.Entry).ToList();
            var result = await _runner.ExportProfileAsync(selected, folder, progress, _cancellation.Token);
            ExportResult =
                $"Wrote the InDows profile: {result.AppsActive:N0} app(s) reinstall automatically" +
                (result.AppsCommented > 0 ? $" ({result.AppsCommented:N0} to review)" : "") +
                $", {result.SettingsRead:N0} settings captured" +
                (result.SettingsManual > 0 ? $" ({result.SettingsManual:N0} need a manual touch)" : "") +
                $". Drop this folder into InDows. → {result.Folder}";
            ProgressText = "";
        }
        catch (OperationCanceledException)
        {
            ProgressText = "Export cancelled — a partial profile may be in the folder.";
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            ProgressText = "";
        }
        finally
        {
            IsBusy = false;
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }

    public void Cancel()
    {
        ProgressText = "Cancelling…";
        _cancellation?.Cancel();
    }

    private void SetAll(bool selected)
    {
        _suppressSelectionEvents = true;
        foreach (var app in Apps)
        {
            app.IsSelected = selected;
        }

        _suppressSelectionEvents = false;
        SelectionChanged?.Invoke(); // one save for the whole bulk toggle
    }

    private void ApplyPendingDeselect()
    {
        if (_pendingDeselect is not { } keys)
        {
            return;
        }

        var deselected = keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _suppressSelectionEvents = true;
        foreach (var app in Apps)
        {
            app.IsSelected = !deselected.Contains(app.Entry.Key);
        }

        _suppressSelectionEvents = false;
        _pendingDeselect = null;
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppRowViewModel.IsSelected) && !_suppressSelectionEvents)
        {
            SelectionChanged?.Invoke();
        }
    }
}
