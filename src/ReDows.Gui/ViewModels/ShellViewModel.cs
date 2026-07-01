using ReDows.Gui.Apps;
using ReDows.Gui.Backup;
using ReDows.Gui.Context;
using ReDows.Gui.Navigation;
using ReDows.Gui.Restore;
using ReDows.Gui.Reviewing;
using ReDows.Gui.Scanning;
using ReDows.Gui.Session;

namespace ReDows.Gui.ViewModels;

/// <summary>
/// The window shell: owns the screens and the current one. Nav buttons bind to the Show* commands;
/// the content area binds to <see cref="CurrentViewModel"/> (a DataTemplate maps each view-model to
/// its view). It also remembers the last session — after a scan (and whenever the review trash changes)
/// it persists a session; on start-up, if one exists, Home offers to resume it (scan + decisions).
/// </summary>
public sealed class ShellViewModel : ViewModelBase
{
    private readonly ISessionStore _sessionStore;

    private object _currentViewModel;
    private SessionFile? _pendingSession;
    private bool _hasPendingSession;
    private string _sessionSummary = "";
    private string? _scannedUtc;

    public ShellViewModel(IContextSource contextSource, IScanRunner scanRunner, IFolderBrowser folderBrowser, IModuleCatalog moduleCatalog, IBackupRunner backupRunner, IRestoreRunner restoreRunner, IAppsRunner appsRunner, ISessionStore sessionStore)
    {
        _sessionStore = sessionStore;
        Home = new HomeViewModel(contextSource);
        Scan = new ScanViewModel(scanRunner, moduleCatalog);
        Review = new ReviewViewModel(folderBrowser);
        Backup = new BackupViewModel(backupRunner);
        Restore = new RestoreViewModel(restoreRunner);
        Apps = new AppsViewModel(appsRunner);
        _currentViewModel = Home;

        // Persist the session on the two signals that change it: a finished scan, and a trash decision.
        Scan.Scanned += OnScanned;
        Review.TrashChanged += SaveSession;

        ShowHomeCommand = new RelayCommand(_ => CurrentViewModel = Home);
        ShowScanCommand = new RelayCommand(_ => CurrentViewModel = Scan);
        ShowReviewCommand = new RelayCommand(_ =>
        {
            Review.SetRoots(ReviewRootsFromScan(), scanned: Scan.Result is not null);
            CurrentViewModel = Review;
        });
        ShowBackupCommand = new RelayCommand(_ =>
        {
            // Feed the Backup screen the manifest the last scan wrote (null if nothing scanned yet) and
            // the review trash, so it copies the kept-minus-trash selection.
            Backup.ManifestPath = Scan.Result?.ManifestPath;
            Backup.ExcludedPaths = Review.Trash.Items.Keys.ToList();
            CurrentViewModel = Backup;
        });
        ShowRestoreCommand = new RelayCommand(_ => CurrentViewModel = Restore);
        ShowAppsCommand = new RelayCommand(_ =>
        {
            CurrentViewModel = Apps;
            _ = Apps.LoadAsync(); // loads once on first visit (winget enrichment runs in the background)
        });
        ResumeCommand = new RelayCommand(_ => Resume());
        DiscardSessionCommand = new RelayCommand(_ => DiscardSession());
    }

    public HomeViewModel Home { get; }

    public ScanViewModel Scan { get; }

    public ReviewViewModel Review { get; }

    public BackupViewModel Backup { get; }

    public RestoreViewModel Restore { get; }

    public AppsViewModel Apps { get; }

    public RelayCommand ShowHomeCommand { get; }

    public RelayCommand ShowScanCommand { get; }

    public RelayCommand ShowReviewCommand { get; }

    public RelayCommand ShowBackupCommand { get; }

    public RelayCommand ShowRestoreCommand { get; }

    public RelayCommand ShowAppsCommand { get; }

    public RelayCommand ResumeCommand { get; }

    public RelayCommand DiscardSessionCommand { get; }

    public object CurrentViewModel
    {
        get => _currentViewModel;
        private set => Set(ref _currentViewModel, value);
    }

    /// <summary>True when a previous session was found on start-up — Home shows the "welcome back" card.</summary>
    public bool HasPendingSession
    {
        get => _hasPendingSession;
        private set => Set(ref _hasPendingSession, value);
    }

    /// <summary>One line describing the previous session (when, how much kept, how much trashed).</summary>
    public string SessionSummary
    {
        get => _sessionSummary;
        private set => Set(ref _sessionSummary, value);
    }

    /// <summary>Load the initial screen's data once the window is shown, and detect a resumable session.</summary>
    public void Initialize()
    {
        Home.Load();

        _pendingSession = _sessionStore.Load();
        if (_pendingSession is { } session)
        {
            SessionSummary = Describe(session);
            HasPendingSession = true;
        }
    }

    /// <summary>Resume the saved session without re-scanning: restore the scan summary, the trash, and go to Review.</summary>
    private void Resume()
    {
        if (_pendingSession is not { } session)
        {
            return;
        }

        _scannedUtc = session.ScannedUtc;
        Scan.Restore(SessionSnapshot.ToResultView(session));
        Review.RestoreTrash(session.Trash.ToDictionary(item => item.Path, item => item.Bytes, StringComparer.OrdinalIgnoreCase));
        Backup.ManifestPath = session.ManifestPath;
        Backup.ExcludedPaths = session.Trash.Select(item => item.Path).ToList();
        Review.SetRoots(ReviewRootsFromScan(), scanned: true);

        CurrentViewModel = Review;
        HasPendingSession = false;
    }

    private void DiscardSession()
    {
        _sessionStore.Clear();
        _pendingSession = null;
        HasPendingSession = false;
    }

    private void OnScanned()
    {
        _scannedUtc = DateTime.UtcNow.ToString("o");
        SaveSession();
    }

    private void SaveSession()
    {
        var result = Scan.Result;
        if (result?.ManifestPath is null)
        {
            return; // nothing to persist until a scan has written a manifest
        }

        var session = SessionSnapshot.Build(result, Review.Trash.Items, Scan.WholePc ? null : Scan.FolderPath, _scannedUtc ?? DateTime.UtcNow.ToString("o"));
        _sessionStore.Save(session);
    }

    private static string Describe(SessionFile session)
    {
        var when = "recently";
        if (DateTime.TryParse(session.ScannedUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var utc))
        {
            when = utc.ToLocalTime().ToString("g");
        }

        return $"Your last scan ({when}) — keeping {session.KeepText} · {session.Trash.Count:N0} item(s) you trashed.";
    }

    /// <summary>The latest scan's REVIEW head directories, as explorer roots (backslash-normalized).</summary>
    private IReadOnlyList<EntryRow> ReviewRootsFromScan() =>
        (Scan.Result?.TopReview ?? [])
        .Select(row =>
        {
            var path = row.Folder.Replace('/', '\\');
            return new EntryRow(path, path, IsDirectory: true, row.Bytes);
        })
        .ToList();
}
