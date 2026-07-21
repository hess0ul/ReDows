using System.Collections.ObjectModel;
using System.ComponentModel;
using ReDows.Core.Scanning;
using ReDows.Gui.Navigation;
using ReDows.Gui.Scanning;

namespace ReDows.Gui.ViewModels;

// LockedFilesGroup lives in ReDows.Core.Ai; used to ask the AI how to keep the "export before reset" files.

/// <summary>
/// The Scan screen's brain. It runs the scan off the UI thread (so the window never freezes),
/// streams progress, and lets the user Cancel (the engine then returns a partial result). All
/// state (running / done / partial / error) is plain and testable off a fake <see cref="IScanRunner"/>.
/// </summary>
public sealed class ScanViewModel : ViewModelBase
{
    private readonly IScanRunner _runner;
    private readonly IModuleSettingsStore? _moduleSettings;
    private CancellationTokenSource? _cancellation;
    private CancellationTokenSource? _adviceCancellation; // the "how to keep the locked files" AI call

    private bool _isAdvising;
    private string _adviceText = "";
    private string _adviceBusyText = "";

    private bool _wholePc = true;
    private string _folderPath = "";
    private bool _recognizeInstalledApps = true;
    private bool _useGameSaveCatalog;
    private bool _findDuplicates;
    private bool _duplicatesGlobal = true;
    private bool _isRunning;
    private string _progressText = "";
    private ScanResultView? _result;
    private string? _error;

    public ScanViewModel(IScanRunner runner, IModuleCatalog moduleCatalog, IModuleSettingsStore? moduleSettings = null, AiAssistantViewModel? ai = null)
    {
        _runner = runner;
        _moduleSettings = moduleSettings;
        Ai = ai;

        // Re-apply the keep/review/ignore choices the user last made (so they don't retype them every
        // launch); subscribe AFTER applying, so restoring a saved choice doesn't count as a fresh change.
        var saved = moduleSettings?.Load() ?? new Dictionary<string, string>();
        Modules = new ObservableCollection<ModuleRowViewModel>(
            moduleCatalog.Load().Select(definition =>
            {
                var row = new ModuleRowViewModel(definition);
                if (saved.TryGetValue(definition.Name, out var action) && Enum.TryParse<ModuleAction>(action, ignoreCase: true, out var parsed))
                {
                    row.Action = parsed;
                }

                row.PropertyChanged += OnModuleActionChanged;
                return row;
            }));

        RunCommand = new RelayCommand(async _ => await RunAsync(), _ => !IsRunning && ScopeIsValid());
        CancelCommand = new RelayCommand(_ => Cancel(), _ => IsRunning);

        // "How do I keep these?" over the machine-bound (DPAPI) files: clickable whenever there are any;
        // if the AI isn't set up, the click routes to Settings (same as Review's AI buttons).
        AdviseCommand = new RelayCommand(_ => AdviseOrRedirect(), _ => !IsAdvising && Result?.LockedFiles is { Count: > 0 });
        CancelAdviceCommand = new RelayCommand(_ => _adviceCancellation?.Cancel(), _ => IsAdvising);
        ClearAdviceCommand = new RelayCommand(_ => AdviceText = "");
    }

    /// <summary>The shared AI assistant (null in tests that don't exercise it); configured on the Settings screen.</summary>
    public AiAssistantViewModel? Ai { get; }

    /// <summary>Raised when the user asks the AI but it isn't set up yet; the shell opens the Settings screen.</summary>
    public event Action? AiSetupRequested;

    /// <summary>Persist the per-category choices whenever one changes, so next launch starts where you left off.</summary>
    private void OnModuleActionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ModuleRowViewModel.Action))
        {
            _moduleSettings?.Save(Modules.ToDictionary(
                module => module.Name,
                module => module.Action.ToString().ToLowerInvariant()));
        }
    }

    /// <summary>The category modules (games, media...) the user can set to keep / review / ignore before scanning.</summary>
    public ObservableCollection<ModuleRowViewModel> Modules { get; }

    public RelayCommand RunCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand AdviseCommand { get; }

    public RelayCommand CancelAdviceCommand { get; }

    public RelayCommand ClearAdviceCommand { get; }

    /// <summary>True while the AI is answering "how do I keep these"; drives the progress row and Cancel.</summary>
    public bool IsAdvising
    {
        get => _isAdvising;
        private set
        {
            Set(ref _isAdvising, value);
            AdviseCommand.RaiseCanExecuteChanged();
            CancelAdviceCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>The AI's answer (or an error), shown in a card under the "export before reset" list; empty = none.</summary>
    public string AdviceText
    {
        get => _adviceText;
        private set { Set(ref _adviceText, value); Raise(nameof(HasAdvice)); }
    }

    public bool HasAdvice => AdviceText.Length > 0;

    /// <summary>What the advice call is doing right now, shown while it runs.</summary>
    public string AdviceBusyText
    {
        get => _adviceBusyText;
        private set => Set(ref _adviceBusyText, value);
    }

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
    /// the CLI's --no-reinstall; everything stays in review.
    /// </summary>
    public bool RecognizeInstalledApps
    {
        get => _recognizeInstalledApps;
        set => Set(ref _recognizeInstalledApps, value);
    }

    /// <summary>
    /// Use the optional ludusavi game-save catalog (off by default): the manifest of per-game save
    /// locations is downloaded onto this PC (first use) and cached, then the save folders that actually
    /// exist here are kept automatically. Its data is PCGamingWiki's (CC BY-NC-SA), never bundled.
    /// </summary>
    public bool UseGameSaveCatalog
    {
        get => _useGameSaveCatalog;
        set => Set(ref _useGameSaveCatalog, value);
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
        private set
        {
            Set(ref _result, value);
            Raise(nameof(HasReview));
            Raise(nameof(HasLockedFiles));
            AdviceText = ""; // a new scan (or a restore) starts with no advice shown
            AdviseCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>True when the last scan flagged machine-bound (DPAPI) files to export before the reset.</summary>
    public bool HasLockedFiles => Result?.LockedFiles is { Count: > 0 };

    /// <summary>
    /// True when the last scan flagged something to review. When false after a scan, there is nothing to
    /// sort by hand. The "Review these" button becomes "Back up what I'm keeping" and skips Review.
    /// </summary>
    public bool HasReview => Result?.TopReview.Count > 0;

    /// <summary>Raised after a scan finishes successfully. The shell persists the session on this signal.</summary>
    public event Action? Scanned;

    /// <summary>Put a result back without scanning (resuming a saved session): drives Review + Backup.</summary>
    public void Restore(ScanResultView result) => Result = result;

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
        ProgressText = "Starting...";
        IsRunning = true;
        _cancellation = new CancellationTokenSource();
        var progress = new Progress<ScanProgress>(p => ProgressText = $"{p.Items:N0} items: {p.CurrentPath}");
        try
        {
            var request = new ScanRequest(
                WholePc ? null : FolderPath,
                Modules.Select(module => module.ToCategoryModule()).ToList(),
                BuildDuplicateScan(),
                RecognizeInstalledApps,
                UseGameSaveCatalog);
            Result = await _runner.RunAsync(request, progress, _cancellation.Token);
            ProgressText = Result.Partial ? "Interrupted. Partial figures below." : "Done.";
            Scanned?.Invoke(); // let the shell persist the session (scan summary + manifest to back up)
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
        ProgressText = "Cancelling...";
        _cancellation?.Cancel();
    }

    /// <summary>"How do I keep these" click: run the advice if the AI is set up, else route to Settings.</summary>
    private void AdviseOrRedirect()
    {
        if (Ai is null || !Ai.IsEnabled)
        {
            AiSetupRequested?.Invoke();
            return;
        }

        _ = AdviseAsync();
    }

    /// <summary>
    /// Ask the AI how to keep the useful data behind the machine-bound (DPAPI) files (names/paths only,
    /// never contents). The answer (or an error) shows in a card; Cancel stops it. A hostile reply is
    /// length-capped. Public so a test can drive it off a fake assistant.
    /// </summary>
    public async Task AdviseAsync()
    {
        if (Ai is null || !Ai.IsEnabled || IsAdvising || Result?.LockedFiles is not { Count: > 0 } groups)
        {
            return;
        }

        AdviceText = "";
        AdviceBusyText = "Asking the AI how to keep these...";
        IsAdvising = true;
        _adviceCancellation = new CancellationTokenSource();
        try
        {
            var advice = await Ai.AdviseAsync(groups, _adviceCancellation.Token);
            if (advice is null)
            {
                return; // the assistant was turned off mid-call
            }

            var trimmed = advice.Trim();
            AdviceText = trimmed.Length == 0
                ? "The AI returned an empty answer."
                : trimmed.Length > MaxAdviceLength ? trimmed[..MaxAdviceLength] + "..." : trimmed;
        }
        catch (OperationCanceledException) when (_adviceCancellation?.IsCancellationRequested == true)
        {
            // cancelled by the user; leave the card empty
        }
        catch (Exception ex)
        {
            AdviceText = "The AI could not answer: " + (ex is OperationCanceledException ? "it timed out." : ex.Message);
        }
        finally
        {
            IsAdvising = false;
            AdviceBusyText = "";
            _adviceCancellation?.Dispose();
            _adviceCancellation = null;
        }
    }

    private const int MaxAdviceLength = 8000; // bound a hostile endpoint's reply for display

    private void RaiseCommands()
    {
        RunCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
    }
}
