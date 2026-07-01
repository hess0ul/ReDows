using System.Collections.ObjectModel;
using ReDows.Gui.Navigation;
using ReDows.Gui.Scanning;

namespace ReDows.Gui.ViewModels;

/// <summary>
/// The Scan screen's brain. It runs the scan off the UI thread (so the window never freezes),
/// streams progress, and lets the user Cancel (the engine then returns a partial result). All
/// state — running / done / partial / error — is plain and testable off a fake <see cref="IScanRunner"/>.
/// </summary>
public sealed class ScanViewModel : ViewModelBase
{
    private readonly IScanRunner _runner;
    private CancellationTokenSource? _cancellation;

    private bool _wholePc = true;
    private string _folderPath = "";
    private bool _recognizeInstalledApps = true;
    private bool _findDuplicates;
    private bool _duplicatesGlobal = true;
    private bool _isRunning;
    private string _progressText = "";
    private ScanResultView? _result;
    private string? _error;

    public ScanViewModel(IScanRunner runner, IModuleCatalog moduleCatalog)
    {
        _runner = runner;
        Modules = new ObservableCollection<ModuleRowViewModel>(
            moduleCatalog.Load().Select(definition => new ModuleRowViewModel(definition)));
        RunCommand = new RelayCommand(async _ => await RunAsync(), _ => !IsRunning && ScopeIsValid());
        CancelCommand = new RelayCommand(_ => Cancel(), _ => IsRunning);
    }

    /// <summary>The category modules (games, media…) the user can set to keep / review / ignore before scanning.</summary>
    public ObservableCollection<ModuleRowViewModel> Modules { get; }

    public RelayCommand RunCommand { get; }

    public RelayCommand CancelCommand { get; }

    public bool WholePc
    {
        get => _wholePc;
        set { Set(ref _wholePc, value); RaiseCommands(); }
    }

    public string FolderPath
    {
        get => _folderPath;
        set { Set(ref _folderPath, value); RaiseCommands(); }
    }

    /// <summary>
    /// Recognize this PC's installed apps (on by default, like the CLI): their install folders become
    /// re-downloadable (ignored where the scan would only review) and their settings are kept. Off =
    /// the CLI's --no-reinstall — everything stays in review.
    /// </summary>
    public bool RecognizeInstalledApps
    {
        get => _recognizeInstalledApps;
        set => Set(ref _recognizeInstalledApps, value);
    }

    /// <summary>Also hunt byte-identical files during the scan (a slower extra pass; read-only).</summary>
    public bool FindDuplicates
    {
        get => _findDuplicates;
        set => Set(ref _findDuplicates, value);
    }

    /// <summary>true = de-duplicate every file; false = only the categories ticked in <see cref="Modules"/> (per type).</summary>
    public bool DuplicatesGlobal
    {
        get => _duplicatesGlobal;
        set => Set(ref _duplicatesGlobal, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set { Set(ref _isRunning, value); RaiseCommands(); }
    }

    public string ProgressText
    {
        get => _progressText;
        private set => Set(ref _progressText, value);
    }

    public ScanResultView? Result
    {
        get => _result;
        private set => Set(ref _result, value);
    }

    public string? Error
    {
        get => _error;
        private set => Set(ref _error, value);
    }

    private bool ScopeIsValid() => WholePc || !string.IsNullOrWhiteSpace(FolderPath);

    private DuplicateScan? BuildDuplicateScan()
    {
        if (!FindDuplicates)
        {
            return null;
        }

        // Global = every file (null filter); per type = only the extensions of the ticked categories.
        IReadOnlyList<string>? extensions = DuplicatesGlobal
            ? null
            : Modules.Where(module => module.DedupeSelected)
                .SelectMany(module => module.Extensions)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        return new DuplicateScan(true, extensions);
    }

    public async Task RunAsync()
    {
        if (IsRunning)
        {
            return;
        }

        Error = null;
        Result = null;
        ProgressText = "Starting…";
        IsRunning = true;
        _cancellation = new CancellationTokenSource();
        var progress = new Progress<ScanProgress>(p => ProgressText = $"{p.Items:N0} items — {p.CurrentPath}");
        try
        {
            var request = new ScanRequest(
                WholePc ? null : FolderPath,
                Modules.Select(module => module.ToCategoryModule()).ToList(),
                BuildDuplicateScan(),
                RecognizeInstalledApps);
            Result = await _runner.RunAsync(request, progress, _cancellation.Token);
            ProgressText = Result.Partial ? "Interrupted — partial figures below." : "Done.";
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
            IsRunning = false;
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }

    public void Cancel()
    {
        ProgressText = "Cancelling…";
        _cancellation?.Cancel();
    }

    private void RaiseCommands()
    {
        RunCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
    }
}
