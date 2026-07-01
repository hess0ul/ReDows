using System.Collections.ObjectModel;
using ReDows.Gui.Apps;
using ReDows.Gui.Navigation;

namespace ReDows.Gui.ViewModels;

/// <summary>
/// The Apps screen's brain. It loads this PC's installed apps off the UI thread (winget-enriched, so as
/// many as possible can reinstall automatically), lists them with a tick each (all on by default), and
/// writes the chosen ones into an InDows reinstall profile (configuration.dsc.yaml) — the apps half of the
/// ReDows → InDows loop. All state is plain and testable off a fake <see cref="IAppsRunner"/>.
/// </summary>
public sealed class AppsViewModel : ViewModelBase
{
    private readonly IAppsRunner _runner;
    private CancellationTokenSource? _cancellation;

    private bool _isLoading;
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
        CancelCommand = new RelayCommand(_ => Cancel(), _ => IsLoading);
    }

    /// <summary>The installed apps, each with a tick for "reinstall after the reset".</summary>
    public ObservableCollection<AppRowViewModel> Apps { get; } = [];

    public RelayCommand SelectAllCommand { get; }

    public RelayCommand SelectNoneCommand { get; }

    public RelayCommand CancelCommand { get; }

    public bool IsLoading
    {
        get => _isLoading;
        private set { Set(ref _isLoading, value); CancelCommand.RaiseCanExecuteChanged(); }
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

    /// <summary>Load the inventory once (on first visit). Winget enrichment is on so the profile is useful.</summary>
    public async Task LoadAsync()
    {
        if (IsLoading || _loaded)
        {
            return;
        }

        Error = null;
        ExportResult = null;
        ProgressText = "Starting…";
        IsLoading = true;
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
            IsLoading = false;
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }

    /// <summary>Write the ticked apps into an InDows reinstall profile (configuration.dsc.yaml) in a folder.</summary>
    public void Export(string folder)
    {
        Error = null;
        ExportResult = null;
        try
        {
            var selected = Apps.Where(app => app.IsSelected).Select(app => app.Entry).ToList();
            var result = _runner.Export(selected, folder);
            var commented = result.CandidateCount + result.ManualCount;
            ExportResult =
                $"Wrote {result.ActiveCount:N0} app(s) that reinstall automatically to configuration.dsc.yaml" +
                (commented > 0 ? $" ({commented:N0} listed as comments to review — no winget id)" : "") +
                $". Drop this file into InDows to reinstall them. → {result.Path}";
        }
        catch (Exception ex)
        {
            Error = ex.Message;
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
