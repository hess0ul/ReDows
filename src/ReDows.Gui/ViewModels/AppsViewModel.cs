using System.Collections.ObjectModel;
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
            Apps.Clear();
            foreach (var entry in result.Apps)
            {
                Apps.Add(new AppRowViewModel(entry));
            }

            Summary = result.Summary;
            ProgressText = "";
            _loaded = true;
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
        foreach (var app in Apps)
        {
            app.IsSelected = selected;
        }
    }
}
