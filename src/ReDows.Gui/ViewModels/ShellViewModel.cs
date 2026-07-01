using ReDows.Gui.Backup;
using ReDows.Gui.Context;
using ReDows.Gui.Navigation;
using ReDows.Gui.Restore;
using ReDows.Gui.Reviewing;
using ReDows.Gui.Scanning;

namespace ReDows.Gui.ViewModels;

/// <summary>
/// The window shell: owns the screens and the current one. Nav buttons bind to the Show* commands;
/// the content area binds to <see cref="CurrentViewModel"/> (a DataTemplate maps each view-model to
/// its view). Opening Review feeds it the biggest REVIEW folders from the latest scan.
/// </summary>
public sealed class ShellViewModel : ViewModelBase
{
    private object _currentViewModel;

    public ShellViewModel(IContextSource contextSource, IScanRunner scanRunner, IFolderBrowser folderBrowser, IModuleCatalog moduleCatalog, IBackupRunner backupRunner, IRestoreRunner restoreRunner)
    {
        Home = new HomeViewModel(contextSource);
        Scan = new ScanViewModel(scanRunner, moduleCatalog);
        Review = new ReviewViewModel(folderBrowser);
        Backup = new BackupViewModel(backupRunner);
        Restore = new RestoreViewModel(restoreRunner);
        _currentViewModel = Home;

        ShowHomeCommand = new RelayCommand(_ => CurrentViewModel = Home);
        ShowScanCommand = new RelayCommand(_ => CurrentViewModel = Scan);
        ShowReviewCommand = new RelayCommand(_ =>
        {
            Review.SetRoots(ReviewRootsFromScan());
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
    }

    public HomeViewModel Home { get; }

    public ScanViewModel Scan { get; }

    public ReviewViewModel Review { get; }

    public BackupViewModel Backup { get; }

    public RestoreViewModel Restore { get; }

    public RelayCommand ShowHomeCommand { get; }

    public RelayCommand ShowScanCommand { get; }

    public RelayCommand ShowReviewCommand { get; }

    public RelayCommand ShowBackupCommand { get; }

    public RelayCommand ShowRestoreCommand { get; }

    public object CurrentViewModel
    {
        get => _currentViewModel;
        private set => Set(ref _currentViewModel, value);
    }

    /// <summary>Load the initial screen's data once the window is shown.</summary>
    public void Initialize() => Home.Load();

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
