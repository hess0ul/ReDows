using System.Collections.ObjectModel;
using ReDows.Core.Ai;
using ReDows.Core.Triage;
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
/// by forgetting). You walk the REVIEW folders one at a time (Folder X of N, Next), drill in
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
    private CancellationTokenSource? _filesCancellation; // per-file colour pass — its own Cancel
    private readonly FileTriage? _triage; // fast-path rules: colour obvious entries without an AI call
    private readonly Dictionary<string, (string Key, string Reason)> _fileImportance = new(StringComparer.OrdinalIgnoreCase); // path → colour+why, this folder
    private bool _isAnalyzingFiles;
    private string _filesBusyText = "";
    private string _filesSummary = "";
    private string _cloudReminder = "";
    private ReviewSort _sort = ReviewSort.Size;

    private bool _scanned;
    private bool _isLoading;
    private bool _isTrashOpen;
    private string? _error;
    private string _location = "No scan yet — run a scan first, then come here.";
    private string _stepText = "";
    private string _folderNote = "";
    private string _learnedNote = "";

    public ReviewViewModel(IFolderBrowser browser, AiAssistantViewModel? ai = null, FileTriage? triage = null)
    {
        _browser = browser;
        Ai = ai;
        _triage = triage;
        if (ai is not null)
        {
            // The user accepted a "safe to drop" suggestion → same gesture as "Drop this folder",
            // but ONLY if the folder the model analyzed is still the one on screen (a slow reply must
            // never trash a folder the AI didn't look at), and never while a folder load is in flight.
            ai.DropRequested += analyzedFolder =>
            {
                if (!IsLoading && HasFolder && string.Equals(analyzedFolder, _trail[^1].Path, StringComparison.OrdinalIgnoreCase))
                {
                    ai.Learn(analyzedFolder, _trail[^1].Bytes); // an ACCEPTED drop is remembered for future scans
                    _ = DropCurrentFolderAsync();
                }
            };
        }

        AnalyzeFolderCommand = new RelayCommand(_ => _ = AnalyzeCurrentFolderAsync(), _ => Ai is not null && HasFolder && !IsLoading);
        AnalyzeAllCommand = new RelayCommand(_ => _ = AnalyzeAllFoldersAsync(), _ => Ai is not null && HasRoots && !IsLoading);
        AnalyzeFilesCommand = new RelayCommand(_ => _ = AnalyzeFilesHereAsync(), _ => (_triage is not null || Ai is not null) && HasFolder && Entries.Count > 0 && !IsLoading && !IsAnalyzingFiles);
        CancelFilesCommand = new RelayCommand(_ => _filesCancellation?.Cancel(), _ => IsAnalyzingFiles);
        OpenCommand = new RelayCommand(item => { if (item is EntryRow entry) _ = OpenAsync(entry); }, _ => !IsLoading);
        DropCommand = new RelayCommand(item => { if (item is EntryRow entry) DropEntry(entry); }, _ => !IsLoading);
        RestoreCommand = new RelayCommand(item => { if (item is TrashRow trashed) _ = RestoreAsync(trashed); });
        UpCommand = new RelayCommand(_ => _ = UpAsync(), _ => !AtFolderRoot && !IsLoading);
        CancelCommand = new RelayCommand(_ => _cancellation?.Cancel(), _ => IsLoading);
        NextCommand = new RelayCommand(_ => _ = NextAsync(), _ => HasNext && !IsLoading);
        DropFolderCommand = new RelayCommand(_ => _ = DropCurrentFolderAsync(), _ => HasFolder && !IsLoading);
        ToggleTrashCommand = new RelayCommand(_ => IsTrashOpen = !IsTrashOpen);
        SortBySizeCommand = new RelayCommand(_ => SetSort(ReviewSort.Size));
        SortByNameCommand = new RelayCommand(_ => SetSort(ReviewSort.Name));
        SortByTypeCommand = new RelayCommand(_ => SetSort(ReviewSort.Type));
    }

    public DropSelection Trash { get; } = new();

    /// <summary>The optional AI assistant (null in tests that don't exercise it — its card stays hidden).</summary>
    public AiAssistantViewModel? Ai { get; }

    public RelayCommand AnalyzeFolderCommand { get; }

    public RelayCommand AnalyzeAllCommand { get; }

    /// <summary>Colour every entry in THIS folder by importance (one AI call per entry, in folder context).</summary>
    public RelayCommand AnalyzeFilesCommand { get; }

    public RelayCommand CancelFilesCommand { get; }

    /// <summary>True while the per-file colour pass runs — drives its progress row and Cancel.</summary>
    public bool IsAnalyzingFiles
    {
        get => _isAnalyzingFiles;
        private set
        {
            Set(ref _isAnalyzingFiles, value);
            AnalyzeFilesCommand.RaiseCanExecuteChanged();
            CancelFilesCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>What the per-file pass is doing right now (folder X of N — name).</summary>
    public string FilesBusyText
    {
        get => _filesBusyText;
        private set => Set(ref _filesBusyText, value);
    }

    /// <summary>After the pass: how many entries a rule coloured vs the AI vs left unknown.</summary>
    public string FilesSummary
    {
        get => _filesSummary;
        private set => Set(ref _filesSummary, value);
    }

    /// <summary>Set when a cloud-synced folder was met — reminds the user to sync before trusting the drop.</summary>
    public string CloudReminder
    {
        get => _cloudReminder;
        private set => Set(ref _cloudReminder, value);
    }

    /// <summary>Raised when the user drops or restores something — the shell persists the decision on this signal.</summary>
    public event Action? TrashChanged;

    /// <summary>Re-apply a saved session's trash (path → size) without raising a change — resuming, not deciding.</summary>
    public void RestoreTrash(IReadOnlyDictionary<string, long> trash)
    {
        foreach (var (path, bytes) in trash)
        {
            Trash.Drop(path, bytes);
        }

        RefreshTrash();
        RaiseSummary();
    }

    public ObservableCollection<EntryRow> Entries { get; } = [];

    public ObservableCollection<TrashRow> TrashItems { get; } = [];

    public RelayCommand OpenCommand { get; }

    public RelayCommand DropCommand { get; }

    public RelayCommand RestoreCommand { get; }

    public RelayCommand UpCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand NextCommand { get; }

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

    /// <summary>
    /// Whether to offer "Back up →" instead of "Next": at the end of the wizard, OR right after a scan
    /// that flagged nothing to review (no folders to walk) — so the user is never stuck on a dead "Next".
    /// </summary>
    public bool ShowBackUp => OnLastFolder || (_scanned && !HasRoots);

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

    /// <summary>How many folders this review pre-trashed from remembered (accepted) AI decisions.</summary>
    public string LearnedNote
    {
        get => _learnedNote;
        private set => Set(ref _learnedNote, value);
    }

    public bool IsTrashOpen
    {
        get => _isTrashOpen;
        set => Set(ref _isTrashOpen, value);
    }

    public string TrashButtonText => $"🗑 Trash ({Trash.DroppedCount:N0})";

    public string KeptSummary => !HasRoots
        ? (_scanned
            ? "Nothing to review — everything is either kept or safe to ignore. You're all set."
            : "No scan yet — run a scan first, then come back to sort what needs a look.")
        : Trash.DroppedCount == 0
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

    /// <summary>
    /// Feed the biggest REVIEW folders from the latest scan and start at the first one. <paramref name="scanned"/>
    /// tells apart "no scan run yet" from "a scan ran but flagged nothing to review" — both give empty roots,
    /// but they need different messages (otherwise a clean scan wrongly reads as "no scan yet").
    /// </summary>
    public void SetRoots(IReadOnlyList<EntryRow> roots, bool scanned = false)
    {
        _roots = roots;
        _scanned = scanned;
        _folderIndex = -1;
        _trail.Clear();
        _totalReviewBytes = roots.Sum(r => r.Bytes);
        Error = null;
        IsTrashOpen = false;
        Ai?.Reset();
        RefreshTrash();
        Raise(nameof(HasRoots));

        LearnedNote = "";
        if (scanned)
        {
            ApplyLearnedDrops(roots);
        }

        if (roots.Count == 0)
        {
            _current = [];
            Entries.Clear();
            StepText = "";
            FolderNote = "";
            Location = scanned
                ? "Nothing to review — your scan sorted everything into keep or ignore. Nothing here needs a second look."
                : "No scan yet — run a scan first, then come here.";
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

    /// <summary>
    /// Ask the AI assistant about the CURRENT folder, from its already-listed rows — names and sizes
    /// only, exactly what is on screen (no extra disk read, no file content).
    /// </summary>
    public async Task AnalyzeCurrentFolderAsync()
    {
        if (Ai is null || !HasFolder)
        {
            return;
        }

        var children = Entries.Select(e => (e.Name, e.IsDirectory, e.Bytes)).ToList();
        await Ai.AnalyzeAsync(_trail[^1].Path, children);
    }

    /// <summary>
    /// "Analyze all": run the assistant over EVERY review folder in sequence (fresh read-only listings
    /// via the browser), storing one suggestion per folder — the wizard shows each one as you land on
    /// its folder, and a summary line counts them. Nothing is dropped without a per-folder Accept.
    /// </summary>
    public async Task AnalyzeAllFoldersAsync()
    {
        if (Ai is null || !HasRoots)
        {
            return;
        }

        await Ai.AnalyzeAllAsync(
            _roots.Select(r => (r.FullPath, r.Name)).ToList(),
            async (path, ct) =>
            {
                var rows = await _browser.ListAsync(path, ct);
                return (IReadOnlyList<(string, bool, long)>)rows.Select(e => (e.Name, e.IsDirectory, e.Bytes)).ToList();
            });

        if (HasFolder)
        {
            Ai.ShowStoredFor(_trail[^1].Path); // the folder on screen gets its fresh badge right away
        }
    }

    /// <summary>
    /// Colour every entry in the CURRENT folder by importance: one AI call per entry, each given the
    /// folder as context (its full listing — the "tree" — and, if we have it, the folder's own verdict).
    /// Bounded to the entries on screen (never the whole PC). Progress + Cancel; two failures in a row
    /// stop the pass. Nothing is dropped — this only tints the rows to guide the eye.
    /// </summary>
    public async Task AnalyzeFilesHereAsync()
    {
        if ((_triage is null && Ai is null) || !HasFolder || Entries.Count == 0 || IsAnalyzingFiles)
        {
            return;
        }

        var folderPath = _trail[^1].Path;
        var parent = AiPayload.Build(folderPath, _current.Select(e => (e.Name, e.IsDirectory, e.Bytes)));
        var parentContext = Ai?.StoredExplanationFor(folderPath); // the folder's own verdict, as context

        var targets = Entries.ToList(); // snapshot of the rows visible now
        IsAnalyzingFiles = true;
        FilesSummary = "";
        CloudReminder = "";
        _filesCancellation = new CancellationTokenSource();
        var token = _filesCancellation.Token;
        var consecutiveFailures = 0;
        int byRule = 0, byAi = 0, cloud = 0;
        try
        {
            for (var i = 0; i < targets.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var row = targets[i];

                // FAST PATH FIRST: a rule that recognises the entry colours it and SKIPS the AI entirely.
                var verdict = _triage?.Classify(row.Name, row.IsDirectory, row.Bytes, row.FullPath) ?? TriageVerdict.Unknown;
                if (verdict.IsKnown)
                {
                    _fileImportance[row.FullPath] = (verdict.Importance, verdict.Reason);
                    ColourRow(row.FullPath, verdict.Importance, verdict.Reason);
                    byRule++;
                    if (verdict.IsCloudSync)
                    {
                        cloud++;
                    }

                    continue;
                }

                // Unknown to the rules → the AI decides, if it's on. No AI → leave it uncoloured.
                if (Ai is null || !Ai.IsEnabled)
                {
                    continue;
                }

                FilesBusyText = $"Asking the AI: {i + 1} of {targets.Count} — {row.Name}…";
                try
                {
                    var file = AiPayload.BuildFileInContext(row.FullPath, row.Name, row.IsDirectory, row.Bytes, parent, parentContext);
                    var suggestion = await Ai.AnalyzeFileAsync(file, token);
                    if (suggestion is null)
                    {
                        break; // the assistant was turned off mid-pass
                    }

                    var key = AiAssistantViewModel.ImportanceKeyOf(suggestion);
                    _fileImportance[row.FullPath] = (key, "AI");
                    ColourRow(row.FullPath, key, "AI");
                    byAi++;
                    consecutiveFailures = 0;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    if (++consecutiveFailures >= 2)
                    {
                        Error = "The endpoint keeps failing — colouring stopped. Test the connection, then try again.";
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // cancelled — the colours computed so far are kept
        }
        finally
        {
            IsAnalyzingFiles = false;
            FilesBusyText = "";
            FilesSummary = byRule + byAi == 0 ? "" : $"Coloured {byRule + byAi}: {byRule} by rule · {byAi} by AI.";
            if (cloud > 0)
            {
                CloudReminder = $"{cloud} item(s) are in a cloud-synced folder — make sure they're synced to your cloud before you drop them, so nothing is lost.";
            }

            _filesCancellation?.Dispose();
            _filesCancellation = null;
        }
    }

    /// <summary>Replace a visible row with a colour-tinted copy (record copy — no in-place mutation).</summary>
    private void ColourRow(string fullPath, string key, string reason)
    {
        for (var i = 0; i < Entries.Count; i++)
        {
            if (string.Equals(Entries[i].FullPath, fullPath, StringComparison.OrdinalIgnoreCase))
            {
                Entries[i] = Entries[i] with { ImportanceKey = key, ImportanceReason = reason };
                return;
            }
        }
    }

    public void DropEntry(EntryRow entry)
    {
        Trash.Drop(entry.FullPath, entry.Bytes);
        Entries.Remove(entry);
        RefreshTrash();
        RaiseSummary();
        TrashChanged?.Invoke();
    }

    public async Task DropCurrentFolderAsync()
    {
        if (!HasFolder)
        {
            return;
        }

        Ai?.ClearResult(); // the folder the suggestion was about is going to the trash — the card goes too
        var (path, bytes) = _trail[^1];
        Ai?.Forget(path);
        Trash.Drop(path, bytes);
        RefreshTrash();
        TrashChanged?.Invoke();

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
        Ai?.Unlearn(trashed.FullPath); // "I changed my mind" — the lesson is forgotten too
        RefreshTrash();
        RaiseSummary();
        TrashChanged?.Invoke();
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
        _filesCancellation?.Cancel();  // a per-file colour pass is about THIS folder — stop it on the move
        _fileImportance.Clear();       // colours belong to the folder you were in; a new one starts fresh
        Ai?.ClearResult(); // a suggestion is about ONE folder — never survive navigation
        Ai?.ShowStoredFor(path); // …but a REMEMBERED one (analyze-all) greets you on its folder
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
            Entries.Add(_fileImportance.TryGetValue(entry.FullPath, out var v)
                ? entry with { ImportanceKey = v.Key, ImportanceReason = v.Reason }
                : entry);
        }
    }

    /// <summary>
    /// Pre-trash the folders whose "safe to drop" the user accepted in a PAST scan (the remembered
    /// lessons) — visible and restorable in the trash, never silently ignored. Restoring unlearns.
    /// Only lessons that fall under this review's folders apply.
    /// </summary>
    private void ApplyLearnedDrops(IReadOnlyList<EntryRow> roots)
    {
        if (Ai is null)
        {
            return;
        }

        var applied = 0;
        foreach (var lesson in Ai.LearnedDrops)
        {
            var underARoot = roots.Any(root =>
                string.Equals(lesson.Path, root.FullPath, StringComparison.OrdinalIgnoreCase)
                || lesson.Path.StartsWith(root.FullPath.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
            if (underARoot && !Trash.IsDropped(lesson.Path))
            {
                Trash.Drop(lesson.Path, lesson.Bytes);
                applied++;
            }
        }

        if (applied > 0)
        {
            RefreshTrash(); // the pre-trashed lessons must show in the trash list right away
            RaiseSummary();
            LearnedNote = $"{applied} folder(s) went straight to the trash from AI suggestions you accepted before — restore any to unlearn.";
            TrashChanged?.Invoke(); // these decisions are in effect now — the session persists them
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
        Raise(nameof(ShowBackUp));
        Raise(nameof(AtFolderRoot));
        UpCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        NextCommand.RaiseCanExecuteChanged();
        DropFolderCommand.RaiseCanExecuteChanged();
        AnalyzeFolderCommand.RaiseCanExecuteChanged();
        AnalyzeAllCommand.RaiseCanExecuteChanged();
        AnalyzeFilesCommand.RaiseCanExecuteChanged();
        OpenCommand.RaiseCanExecuteChanged();
        DropCommand.RaiseCanExecuteChanged();
    }
}
