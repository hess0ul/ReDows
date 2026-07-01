using ReDows.Gui.Navigation;
using ReDows.Gui.Restore;

namespace ReDows.Gui.ViewModels;

/// <summary>
/// The Restore screen's brain. It puts a backup folder back — to the original locations or under a folder
/// you pick — off the UI thread, with progress and Cancel. The vault password is passed transiently to
/// <see cref="RunAsync"/> from the view's PasswordBox (never stored). All state is plain and testable off
/// a fake runner. Non-destructive: existing files are skipped, never overwritten.
/// </summary>
public sealed class RestoreViewModel : ViewModelBase
{
    private readonly IRestoreRunner _runner;
    private CancellationTokenSource? _cancellation;

    private string _backupFolder = "";
    private bool _toOriginalLocations = true;
    private string _targetFolder = "";
    private bool _isRunning;
    private string _progressText = "";
    private RestoreResultView? _result;
    private string? _error;

    public RestoreViewModel(IRestoreRunner runner)
    {
        _runner = runner;
        CancelCommand = new RelayCommand(_ => Cancel(), _ => IsRunning);
    }

    public RelayCommand CancelCommand { get; }

    /// <summary>The backup folder to restore from (the one you backed up to).</summary>
    public string BackupFolder
    {
        get => _backupFolder;
        set { Set(ref _backupFolder, value); Raise(nameof(CanRun)); RaiseCommands(); }
    }

    /// <summary>true = put files back where they came from; false = rebuild the tree under <see cref="TargetFolder"/>.</summary>
    public bool ToOriginalLocations
    {
        get => _toOriginalLocations;
        set { Set(ref _toOriginalLocations, value); Raise(nameof(CanRun)); RaiseCommands(); }
    }

    public string TargetFolder
    {
        get => _targetFolder;
        set { Set(ref _targetFolder, value); Raise(nameof(CanRun)); RaiseCommands(); }
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

    public RestoreResultView? Result
    {
        get => _result;
        private set => Set(ref _result, value);
    }

    public string? Error
    {
        get => _error;
        private set => Set(ref _error, value);
    }

    /// <summary>Ready to run: a backup folder is set, a destination is chosen, and no run is in flight.</summary>
    public bool CanRun => !IsRunning
        && !string.IsNullOrWhiteSpace(BackupFolder)
        && (ToOriginalLocations || !string.IsNullOrWhiteSpace(TargetFolder));

    /// <summary>Run the restore. The vault password is passed transiently (from the view's PasswordBox).</summary>
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
        var progress = new Progress<RestoreProgress>(p => ProgressText = $"{p.Items:N0} files — {p.CurrentPath}");
        try
        {
            var request = new RestoreRequest(
                BackupFolder,
                ToOriginalLocations,
                ToOriginalLocations ? null : TargetFolder,
                string.IsNullOrEmpty(vaultPassword) ? null : vaultPassword);
            Result = await _runner.RunAsync(request, progress, _cancellation.Token);
            ProgressText = "Done.";
        }
        catch (OperationCanceledException)
        {
            ProgressText = "Cancelled — what was restored so far is in place.";
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
