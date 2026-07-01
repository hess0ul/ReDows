using ReDows.Gui.Backup;
using ReDows.Gui.Navigation;

namespace ReDows.Gui.ViewModels;

/// <summary>
/// The Backup screen's brain. It copies the last scan's CAPTURE files to a destination the user picks,
/// off the UI thread (so the window never freezes), with progress and Cancel. The vault password is
/// NOT held here — it is passed straight to <see cref="RunAsync"/> from the view's PasswordBox at run
/// time (never stored, never logged; invariant #5). All state is plain and testable off a fake runner.
/// </summary>
public sealed class BackupViewModel : ViewModelBase
{
    private readonly IBackupRunner _runner;
    private CancellationTokenSource? _cancellation;

    private string? _manifestPath;
    private IReadOnlyList<string> _excludedPaths = [];
    private string _destination = "";
    private bool _useVault;
    private bool _useVss;
    private bool _dedupe;
    private bool _isRunning;
    private string _progressText = "";
    private BackupResultView? _result;
    private string? _error;

    public BackupViewModel(IBackupRunner runner)
    {
        _runner = runner;
        _useVss = runner.IsElevated; // default on only when it can actually work (elevated)
        CancelCommand = new RelayCommand(_ => Cancel(), _ => IsRunning);
    }

    public RelayCommand CancelCommand { get; }

    /// <summary>Whether locked-file rescue (VSS) can work this run — drives the note and the toggle's enabled state.</summary>
    public bool IsElevated => _runner.IsElevated;

    /// <summary>The scan manifest to back up (set by the shell from the last scan). Null/empty = nothing scanned yet.</summary>
    public string? ManifestPath
    {
        get => _manifestPath;
        set
        {
            Set(ref _manifestPath, value);
            Raise(nameof(HasManifest));
            Raise(nameof(CanRun));
            RaiseCommands();
        }
    }

    public bool HasManifest => !string.IsNullOrEmpty(ManifestPath);

    /// <summary>The review trash (paths dropped in the sorter). Review items under these are skipped; captures never are.</summary>
    public IReadOnlyList<string> ExcludedPaths
    {
        get => _excludedPaths;
        set => Set(ref _excludedPaths, value);
    }

    public string Destination
    {
        get => _destination;
        set { Set(ref _destination, value); Raise(nameof(CanRun)); RaiseCommands(); }
    }

    /// <summary>Protect capture:secret files in a password-encrypted vault (else they are deferred, never copied in clear).</summary>
    public bool UseVault
    {
        get => _useVault;
        set => Set(ref _useVault, value);
    }

    /// <summary>Rescue locked files from a volume shadow copy (only takes effect when elevated).</summary>
    public bool UseVss
    {
        get => _useVss;
        set => Set(ref _useVss, value);
    }

    /// <summary>Store byte-identical files only once (the most-recent copy) and record the rest in a restore map.</summary>
    public bool Dedupe
    {
        get => _dedupe;
        set => Set(ref _dedupe, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set { Set(ref _isRunning, value); Raise(nameof(CanRun)); RaiseCommands(); }
    }

    public string ProgressText
    {
        get => _progressText;
        private set => Set(ref _progressText, value);
    }

    public BackupResultView? Result
    {
        get => _result;
        private set => Set(ref _result, value);
    }

    public string? Error
    {
        get => _error;
        private set => Set(ref _error, value);
    }

    /// <summary>Ready to run: a manifest exists, a destination is set, and no run is in flight.</summary>
    public bool CanRun => !IsRunning && HasManifest && !string.IsNullOrWhiteSpace(Destination);

    /// <summary>
    /// Run the backup. The vault password is passed in transiently (from the view's PasswordBox) and is
    /// used only if <see cref="UseVault"/> is on — it is never stored on the view-model.
    /// </summary>
    public async Task RunAsync(string? vaultPassword)
    {
        if (!CanRun)
        {
            return;
        }

        Error = null;
        Result = null;
        ProgressText = "Starting…";
        IsRunning = true;
        _cancellation = new CancellationTokenSource();
        var progress = new Progress<BackupProgress>(p => ProgressText = $"{p.Items:N0} entries — {p.CurrentPath}");
        try
        {
            var request = new BackupRequest(ManifestPath!, Destination, UseVault ? vaultPassword : null, UseVss, ExcludedPaths, Dedupe);
            Result = await _runner.RunAsync(request, progress, _cancellation.Token);
            ProgressText = Result.Balanced
                ? "Done — every file copied and verified."
                : "Done — but the accounting did not balance (see below).";
        }
        catch (OperationCanceledException)
        {
            ProgressText = "Cancelled — the partial backup was left in place.";
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

    private void RaiseCommands() => CancelCommand.RaiseCanExecuteChanged();
}
