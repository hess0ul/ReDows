using System.Collections.ObjectModel;
using ReDows.Core.Ai;
using ReDows.Core.Apps;
using ReDows.Core.Memory;
using ReDows.Core.Prescreening;
using ReDows.Gui.Navigation;
using ReDows.Gui.Reviewing;
using ReDows.Gui.Scanning;

namespace ReDows.Gui.ViewModels;

public enum ReviewSort
{
    Size,
    Name,
    Type,
}

/// <summary>The two stages of Review: first sort what belongs to installed apps, then walk the rest.</summary>
public enum ReviewStep
{
    Apps,
    Files,
}

/// <summary>
/// The review wizard, trash model: everything under review is KEPT by default (safe: nothing lost
/// by forgetting). You walk the REVIEW folders one at a time (Folder X of N, Previous/Next), drill in
/// (Open / Up), and DROP the junk. A dropped item leaves the list and goes to the trash, which you can
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
    private CancellationTokenSource? _filesCancellation; // per-file colour pass; its own Cancel
    private readonly FilePrescreener? _prescreen; // fast-path rules: colour obvious entries without an AI call
    private readonly FolderMemory? _memory; // memory of known folders/apps: colour + a rich note, on browse
    private readonly IInstalledAppsSource? _appsSource; // recogniser of app-made folders (ShareX, Adobe...)
    private InstalledAppFolders? _appFolders; // loaded once, lazily, on first browse
    private bool _appsLoaded;
    private readonly Dictionary<string, (string Key, string Reason, bool FromAi)> _fileImportance = new(StringComparer.OrdinalIgnoreCase); // path → colour + why (+ from the AI?), this folder
    private readonly Dictionary<string, Dictionary<string, (string Key, string Reason)>> _aiByFolder = new(StringComparer.OrdinalIgnoreCase); // folder → (entry path → AI colour + its explanation), kept so walking away and back never re-asks the AI
    private bool _isAnalyzingFiles;
    private string _filesBusyText = "";
    private string _filesSummary = "";
    private string _cloudReminder = "";
    private string _memoryNote = "";
    private string _appSortSummary = "";
    private CancellationTokenSource? _aiProposeCancellation; // per-app / all-apps AI proposal; its own Cancel
    private bool _isProposingAi;
    private string _aiProposeBusyText = "";
    private ReviewStep _step = ReviewStep.Apps; // Review opens on the "by app" step, then "the rest"
    private ReviewSort _sort = ReviewSort.Size;

    private bool _scanned;
    private bool _isLoading;
    private bool _isTrashOpen;
    private string? _error;
    private string _location = "No scan yet. Run a scan first, then come here.";
    private string _stepText = "";
    private string _folderNote = "";
    private string _learnedNote = "";

    public ReviewViewModel(IFolderBrowser browser, AiAssistantViewModel? ai = null, FilePrescreener? prescreen = null, FolderMemory? memory = null, IInstalledAppsSource? appsSource = null)
    {
        _browser = browser;
        Ai = ai;
        _prescreen = prescreen;
        _memory = memory;
        _appsSource = appsSource;
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

        // The wizard's AI buttons stay clickable even when the assistant is off: a click then routes to
        // Settings to set it up (its config lives there), just like the "sort by app" AI buttons.
        AnalyzeFolderCommand = new RelayCommand(_ => RunAiOrRedirect(AnalyzeCurrentFolderAsync), _ => HasFolder && !IsLoading);
        AnalyzeAllCommand = new RelayCommand(_ => RunAiOrRedirect(AnalyzeAllFoldersAsync), _ => HasRoots && !IsLoading);
        AnalyzeFilesCommand = new RelayCommand(_ => RunAiOrRedirect(AnalyzeFilesHereAsync), _ => HasFolder && Entries.Count > 0 && !IsLoading && !IsAnalyzingFiles);
        CancelFilesCommand = new RelayCommand(_ => _filesCancellation?.Cancel(), _ => IsAnalyzingFiles);
        OpenCommand = new RelayCommand(item => { if (item is EntryRow entry) _ = OpenAsync(entry); }, _ => !IsLoading);
        DropCommand = new RelayCommand(item => { if (item is EntryRow entry) DropEntry(entry); }, _ => !IsLoading);
        RestoreCommand = new RelayCommand(item => { if (item is TrashRow trashed) _ = RestoreAsync(trashed); });
        UpCommand = new RelayCommand(_ => _ = UpAsync(), _ => !AtFolderRoot && !IsLoading);
        CancelCommand = new RelayCommand(_ => _cancellation?.Cancel(), _ => IsLoading);
        PreviousCommand = new RelayCommand(_ => _ = PreviousAsync(), _ => HasPrevious && !IsLoading);
        NextCommand = new RelayCommand(_ => _ = NextAsync(), _ => HasNext && !IsLoading);
        DropFolderCommand = new RelayCommand(_ => _ = DropCurrentFolderAsync(), _ => HasFolder && !IsLoading);
        ToggleTrashCommand = new RelayCommand(_ => IsTrashOpen = !IsTrashOpen);
        SortBySizeCommand = new RelayCommand(_ => SetSort(ReviewSort.Size));
        SortByNameCommand = new RelayCommand(_ => SetSort(ReviewSort.Name));
        SortByTypeCommand = new RelayCommand(_ => SetSort(ReviewSort.Type));

        // "Sort by app first" panel: keep/drop an app's data as a block, above the folder wizard.
        DropLocationCommand = new RelayCommand(item => { if (item is AppReviewLocationViewModel location) _ = DropLocationAsync(location); }, _ => !IsLoading);
        KeepLocationCommand = new RelayCommand(item => { if (item is AppReviewLocationViewModel location) KeepLocation(location); });
        DropAppCommand = new RelayCommand(item => { if (item is AppReviewGroupViewModel group) _ = BulkAppAsync(group, drop: true, suggestedOnly: false); }, _ => !IsLoading);
        DropSuggestedCommand = new RelayCommand(item => { if (item is AppReviewGroupViewModel group) _ = BulkAppAsync(group, drop: true, suggestedOnly: true); }, _ => !IsLoading);
        KeepAppCommand = new RelayCommand(item => { if (item is AppReviewGroupViewModel group) _ = BulkAppAsync(group, drop: false, suggestedOnly: false); });

        // The two Review stages: sort by app first, then walk the rest folder by folder.
        GoToFilesStepCommand = new RelayCommand(_ => SetStep(ReviewStep.Files));
        GoToAppsStepCommand = new RelayCommand(_ => SetStep(ReviewStep.Apps));

        // The optional AI proposal: refine an app's (or every app's) keep/drop suggestions with the assistant.
        // The buttons stay clickable even when the assistant is off: clicking then jumps to Settings to set
        // it up (its config lives there), instead of doing nothing.
        ProposeAppWithAiCommand = new RelayCommand(item => { if (item is AppReviewGroupViewModel group) ProposeAppOrRedirect(group); }, _ => CanProposeAi);
        ProposeAllAppsWithAiCommand = new RelayCommand(_ => ProposeAllOrRedirect(), _ => CanProposeAi);
        CancelAiProposeCommand = new RelayCommand(_ => _aiProposeCancellation?.Cancel(), _ => IsProposingAi);
    }

    /// <summary>Raised when the user asks for AI but it isn't set up yet; the shell opens the Settings screen.</summary>
    public event Action? AiSetupRequested;

    public DropSelection Trash { get; } = new();

    /// <summary>The optional AI assistant (null in tests that don't exercise it; its card stays hidden).</summary>
    public AiAssistantViewModel? Ai { get; }

    public RelayCommand AnalyzeFolderCommand { get; }

    public RelayCommand AnalyzeAllCommand { get; }

    /// <summary>Colour every entry in THIS folder by importance (one AI call per entry, in folder context).</summary>
    public RelayCommand AnalyzeFilesCommand { get; }

    public RelayCommand CancelFilesCommand { get; }

    /// <summary>True while the per-file colour pass runs; drives its progress row and Cancel.</summary>
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

    /// <summary>What the per-file pass is doing right now (folder X of N: name).</summary>
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

    /// <summary>Set when a cloud-synced folder was met; reminds the user to sync before trusting the drop.</summary>
    public string CloudReminder
    {
        get => _cloudReminder;
        private set => Set(ref _cloudReminder, value);
    }

    /// <summary>What ReDows knows about the CURRENT folder (a rich note from its memory), shown as a banner.</summary>
    public string MemoryNote
    {
        get => _memoryNote;
        private set => Set(ref _memoryNote, value);
    }

    /// <summary>
    /// The free colour for one entry: what ReDows already KNOWS about it: its memory (a recognised
    /// folder/app, with a rich note) first, then the fast-path rules (a short reason). Null when it
    /// recognises nothing (the entry is unknown and only the AI could judge it).
    /// </summary>
    private (string Key, string Reason)? KnownColour(EntryRow row)
    {
        if (_memory?.Describe(row.Name, row.FullPath) is { } known)
        {
            var key = known.Importance
                ?? (_prescreen?.Classify(row.Name, row.IsDirectory, row.Bytes, row.FullPath) is { IsKnown: true } t ? t.Importance : null)
                ?? "maybe"; // recognised but no strong colour → "worth a look"
            return (key, known.Note);
        }

        if (_prescreen?.Classify(row.Name, row.IsDirectory, row.Bytes, row.FullPath) is { IsKnown: true } verdict)
        {
            return (verdict.Importance, verdict.Reason);
        }

        // A FOLDER whose name is an installed app or its publisher (ShareX, Adobe...) → made by that app, not
        // by the user. Review (never keep/drop; the match is best-effort): keep what you made inside, the
        // app recreates the rest. Only for folders whose own name matches, so a chance hit stays rare.
        if (row.IsDirectory && _appFolders?.Match(row.Name) is { } app)
        {
            return ("maybe", $"Looks like {app}'s data folder, made by the app, not by you. Keep what you exported or created inside; the app recreates the rest.");
        }

        return null;
    }

    /// <summary>Raised when the user drops or restores something. The shell persists the decision on this signal.</summary>
    public event Action? TrashChanged;

    /// <summary>Re-apply a saved session's trash (path → size) without raising a change; resuming, not deciding.</summary>
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

    public RelayCommand PreviousCommand { get; }

    public RelayCommand NextCommand { get; }

    public RelayCommand DropFolderCommand { get; }

    public RelayCommand ToggleTrashCommand { get; }

    public RelayCommand SortBySizeCommand { get; }

    public RelayCommand SortByNameCommand { get; }

    public RelayCommand SortByTypeCommand { get; }

    public RelayCommand DropLocationCommand { get; }

    public RelayCommand KeepLocationCommand { get; }

    public RelayCommand DropAppCommand { get; }

    public RelayCommand DropSuggestedCommand { get; }

    public RelayCommand KeepAppCommand { get; }

    public RelayCommand ProposeAppWithAiCommand { get; }

    public RelayCommand ProposeAllAppsWithAiCommand { get; }

    public RelayCommand CancelAiProposeCommand { get; }

    public RelayCommand GoToFilesStepCommand { get; }

    public RelayCommand GoToAppsStepCommand { get; }

    /// <summary>The "by app" stage is showing (step 1 of 2).</summary>
    public bool IsAppsStep => _step == ReviewStep.Apps;

    /// <summary>The "walk the rest" stage is showing (step 2 of 2).</summary>
    public bool IsFilesStep => _step == ReviewStep.Files;

    /// <summary>
    /// Apps whose data landed in this review's head folders, grouped so you can keep or drop a whole
    /// app's data in one gesture BEFORE walking the rest folder by folder. Empty when the inventory is
    /// unavailable or no review folder matches an app name.
    /// </summary>
    public ObservableCollection<AppReviewGroupViewModel> AppGroups { get; } = [];

    public bool HasAppGroups => AppGroups.Count > 0;

    /// <summary>
    /// Whether the AI proposal buttons are clickable: an assistant exists, apps are present, and no
    /// proposal is already running. NOT gated on the assistant being ON, so a click while it is off can
    /// still route the user to Settings to set it up (see <see cref="AiSetupRequested"/>).
    /// </summary>
    public bool CanProposeAi => Ai is not null && HasAppGroups && !IsProposingAi && !IsLoading;

    /// <summary>True while the AI is proposing keep/drop for app locations; drives its progress row and Cancel.</summary>
    public bool IsProposingAi
    {
        get => _isProposingAi;
        private set
        {
            Set(ref _isProposingAi, value);
            Raise(nameof(CanProposeAi));
            ProposeAppWithAiCommand.RaiseCanExecuteChanged();
            ProposeAllAppsWithAiCommand.RaiseCanExecuteChanged();
            CancelAiProposeCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>What the AI proposal is doing right now (app name, location X of N).</summary>
    public string AiProposeBusyText
    {
        get => _aiProposeBusyText;
        private set => Set(ref _aiProposeBusyText, value);
    }

    /// <summary>One line above the app panel: how many apps it groups and how many folders remain for the wizard.</summary>
    public string AppSortSummary
    {
        get => _appSortSummary;
        private set => Set(ref _appSortSummary, value);
    }

    public bool HasRoots => _roots.Count > 0;

    public bool HasFolder => _folderIndex >= 0 && _folderIndex < _roots.Count;

    // Navigation counts only folders still under review: a folder dropped from the "sort by app" panel
    // (or its own "drop this folder") leaves the wizard, so "Folder X of N" and Previous/Next skip it.
    public bool HasPrevious => PrevActiveIndex(_folderIndex) is not null;

    public bool HasNext => NextActiveIndex(_folderIndex) is not null;

    /// <summary>On a folder with no next one: the wizard's end, where "Next" becomes "Back up →".</summary>
    public bool OnLastFolder => HasFolder && !HasNext;

    /// <summary>
    /// Whether to offer "Back up →" instead of "Next": at the end of the wizard, right after a scan that
    /// flagged nothing to review, OR once every review folder has been dropped (e.g. all handled from the
    /// app panel), so the user is never stuck on a dead "Next".
    /// </summary>
    public bool ShowBackUp => OnLastFolder || (_scanned && !HasRoots) || (_scanned && HasRoots && ActiveRootIndices().Count == 0);

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
            ? "Nothing to review. Everything is either kept or safe to ignore. You're all set."
            : "No scan yet. Run a scan first, then come back to sort what needs a look.")
        : Trash.DroppedCount == 0
            ? $"Keeping everything under review (≈ {Format.Bytes(_totalReviewBytes)}). Drop what you don't need."
            : $"Trash: {Trash.DroppedCount:N0} item(s) · {Format.Bytes(Trash.DroppedBytes)}. Keeping ≈ {Format.Bytes(Math.Max(0, _totalReviewBytes - Trash.DroppedBytes))}";

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
    /// tells apart "no scan run yet" from "a scan ran but flagged nothing to review"; both give empty roots,
    /// but they need different messages (otherwise a clean scan wrongly reads as "no scan yet").
    /// </summary>
    public void SetRoots(IReadOnlyList<EntryRow> roots, bool scanned = false, IReadOnlyList<RecognizedZoneRow>? appZones = null)
    {
        _roots = roots;
        _scanned = scanned;
        _folderIndex = -1;
        _trail.Clear();
        _totalReviewBytes = roots.Sum(r => r.Bytes);
        Error = null;
        IsTrashOpen = false;
        _aiByFolder.Clear(); // a new review starts with no remembered AI analyses
        AppGroups.Clear();   // rebuilt below for this scan's folders
        AppSortSummary = "";
        Raise(nameof(HasAppGroups));
        SetStep(ReviewStep.Apps); // always open on the "by app" stage
        Ai?.Reset();
        RefreshTrash();
        Raise(nameof(HasRoots));

        LearnedNote = "";
        if (scanned)
        {
            ApplyLearnedDrops(roots);
        }

        BuildAppGroups(appZones ?? []); // group the app data folders (after learned drops, so the panel reflects them)

        if (roots.Count == 0)
        {
            _current = [];
            Entries.Clear();
            StepText = "";
            FolderNote = "";
            Location = scanned
                ? "Nothing to review. Your scan sorted everything into keep or ignore. Nothing here needs a second look."
                : "No scan yet. Run a scan first, then come here.";
            RaiseSummary();
            RaiseNav();
            return;
        }

        var active = ActiveRootIndices();
        _ = GoToFolderAsync(active.Count > 0 ? active[0] : 0);
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
        UpdateStepText();
        await LoadCurrentAsync();
    }

    public Task PreviousAsync() => PrevActiveIndex(_folderIndex) is int index ? GoToFolderAsync(index) : Task.CompletedTask;

    public Task NextAsync() => NextActiveIndex(_folderIndex) is int index ? GoToFolderAsync(index) : Task.CompletedTask;

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
    /// Ask the AI assistant about the CURRENT folder, from its already-listed rows: names and sizes
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
    /// via the browser), storing one suggestion per folder. The wizard shows each one as you land on
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
    /// folder as context (its full listing (the "tree") and, if we have it, the folder's own verdict).
    /// Bounded to the entries on screen (never the whole PC). Progress + Cancel; two failures in a row
    /// stop the pass. Nothing is dropped; this only tints the rows to guide the eye.
    /// </summary>
    public async Task AnalyzeFilesHereAsync()
    {
        if ((_prescreen is null && Ai is null) || !HasFolder || Entries.Count == 0 || IsAnalyzingFiles)
        {
            return;
        }

        var folderPath = _trail[^1].Path;
        var parent = AiPayload.Build(folderPath, _current.Select(e => (e.Name, e.IsDirectory, e.Bytes)));
        var parentContext = Ai?.StoredExplanationFor(folderPath); // the folder's own verdict, as context
        var aiCache = AiCacheFor(folderPath); // remember this folder's AI colours so we never re-ask on return

        var targets = Entries.ToList(); // snapshot of the rows visible now
        IsAnalyzingFiles = true;
        FilesSummary = "";
        _filesCancellation = new CancellationTokenSource();
        var token = _filesCancellation.Token;
        var consecutiveFailures = 0;
        int byRule = 0, byAi = 0;
        try
        {
            for (var i = 0; i < targets.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var row = targets[i];

                // KNOWN already? Memory + fast-path coloured it for free on browse; count it, skip the AI.
                if (_fileImportance.TryGetValue(row.FullPath, out var already))
                {
                    if (!already.FromAi)
                    {
                        byRule++;
                    }

                    continue;
                }

                // Unknown to memory and the rules → the AI decides, if it's on. No AI → leave it uncoloured.
                if (Ai is null || !Ai.IsEnabled)
                {
                    continue;
                }

                FilesBusyText = $"Asking the AI: {i + 1} of {targets.Count} ({row.Name})...";
                try
                {
                    var file = AiPayload.BuildFileInContext(row.FullPath, row.Name, row.IsDirectory, row.Bytes, parent, parentContext);
                    var suggestion = await Ai.AnalyzeFileAsync(file, token);
                    if (suggestion is null)
                    {
                        break; // the assistant was turned off mid-pass
                    }

                    var key = AiAssistantViewModel.ImportanceKeyOf(suggestion);
                    var reason = AiReason(suggestion); // the model's OWN explanation, so the tooltip helps you decide; not just "AI"
                    _fileImportance[row.FullPath] = (key, reason, FromAi: true);
                    aiCache[row.FullPath] = (key, reason); // cached: returning to this folder re-shows it (with its why), no new call
                    ColourRow(row.FullPath, key, reason);
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
                        Error = "The endpoint keeps failing, so colouring stopped. Test the connection, then try again.";
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // cancelled; the colours computed so far are kept
        }
        finally
        {
            IsAnalyzingFiles = false;
            FilesBusyText = "";
            FilesSummary = byRule + byAi == 0 ? "" : $"Coloured {byRule + byAi}: {byRule} by rule · {byAi} by AI.";
            _filesCancellation?.Dispose();
            _filesCancellation = null;
        }
    }

    /// <summary>
    /// Colour every entry ReDows already KNOWS: its memory (a recognised folder/app + a rich note) and
    /// the fast-path rules, for FREE as the folder loads, so the tree lights up the moment you open it.
    /// Also surfaces the cloud-sync reminder and the memory note for the folder you're standing in.
    /// Unknown entries stay uncoloured; the AI can judge them on demand.
    /// </summary>
    private void ApplyKnownColours(string folderPath)
    {
        // Load the installed-apps recogniser ONCE, on the first folder we colour (the registry read is a
        // small one-time cost; after that it's just dictionary lookups).
        EnsureAppsLoaded();

        var cloud = 0;
        foreach (var entry in _current)
        {
            if (KnownColour(entry) is { } colour)
            {
                _fileImportance[entry.FullPath] = (colour.Key, colour.Reason, FromAi: false);
            }

            if (_prescreen?.Classify(entry.Name, entry.IsDirectory, entry.Bytes, entry.FullPath).IsCloudSync == true)
            {
                cloud++;
            }
        }

        // Re-show any AI colours (with their explanations) computed for this folder earlier this session.
        // Walking into a subfolder and back must not lose them, nor re-ask the model. Memory/rules win, so
        // only entries they left blank.
        if (_aiByFolder.TryGetValue(folderPath, out var aiCache))
        {
            foreach (var entry in _current)
            {
                if (!_fileImportance.ContainsKey(entry.FullPath) && aiCache.TryGetValue(entry.FullPath, out var cached))
                {
                    _fileImportance[entry.FullPath] = (cached.Key, cached.Reason, FromAi: true);
                }
            }
        }

        var coloured = _current.Count(e => _fileImportance.ContainsKey(e.FullPath));
        FilesSummary = coloured == 0
            ? ""
            : $"{coloured} of {_current.Count} coloured automatically{(coloured < _current.Count ? ". “Colour these files” asks the AI about the rest." : ".")}";

        CloudReminder = cloud > 0
            ? $"{cloud} item(s) here are in a cloud-synced folder. Make sure they're synced to your cloud before you drop them, so nothing is lost."
            : "";

        var trimmed = folderPath.TrimEnd('/', '\\');
        var cut = trimmed.LastIndexOfAny(['/', '\\']);
        MemoryNote = _memory?.Describe(cut < 0 ? trimmed : trimmed[(cut + 1)..], folderPath)?.Note ?? "";
    }

    /// <summary>Get (or create) this folder's cache of AI colours (entry path → colour key + explanation).</summary>
    private Dictionary<string, (string Key, string Reason)> AiCacheFor(string folderPath)
    {
        if (!_aiByFolder.TryGetValue(folderPath, out var cache))
        {
            cache = new Dictionary<string, (string Key, string Reason)>(StringComparer.OrdinalIgnoreCase);
            _aiByFolder[folderPath] = cache;
        }

        return cache;
    }

    /// <summary>
    /// The tooltip for an AI-coloured row: the model's OWN explanation, prefixed "AI:" so you know the
    /// source (a suggestion, not a verdict; it can be wrong). "AI" alone told you nothing; this is the why.
    /// </summary>
    private static string AiReason(AiSuggestion suggestion) =>
        string.IsNullOrWhiteSpace(suggestion.Explanation)
            ? "AI suggestion (no explanation given)."
            : "AI: " + suggestion.Explanation.Trim();

    /// <summary>Replace a visible row with a colour-tinted copy (record copy; no in-place mutation).</summary>
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

        Ai?.ClearResult(); // the folder the suggestion was about is going to the trash; the card goes too
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
            FolderNote = "This folder is in the trash. Click Next ▶, or open the trash to restore it.";
            RaiseSummary();
            RaiseNav();
        }
    }

    public async Task RestoreAsync(TrashRow trashed)
    {
        Trash.Restore(trashed.FullPath);
        Ai?.Unlearn(trashed.FullPath); // "I changed my mind"; the lesson is forgotten too
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
        _filesCancellation?.Cancel();  // a per-file colour pass is about THIS folder; stop it on the move
        _fileImportance.Clear();       // colours belong to the folder you were in; a new one starts fresh
        FilesSummary = "";
        Ai?.ClearResult(); // a suggestion is about ONE folder; never survives navigation
        Ai?.ShowStoredFor(path); // ...but a REMEMBERED one (analyze-all) greets you on its folder
        IsLoading = true;
        _cancellation = new CancellationTokenSource();
        try
        {
            _current = await _browser.ListAsync(path, _cancellation.Token);
            ApplyKnownColours(path); // memory + fast-path colour every entry for free, before it's shown
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
    /// lessons), visible and restorable in the trash, never silently ignored. Restoring unlearns.
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
            LearnedNote = $"{applied} folder(s) went straight to the trash from AI suggestions you accepted before. Restore any to unlearn.";
            TrashChanged?.Invoke(); // these decisions are in effect now; the session persists them
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
        SyncAppGroups(); // any drop/restore (wizard, trash, panel or a resumed session) reflects in the app panel
    }

    /// <summary>Load the installed-apps recogniser once (a small registry read), then just dictionary lookups.</summary>
    private void EnsureAppsLoaded()
    {
        if (_appsLoaded)
        {
            return;
        }

        _appsLoaded = true;
        _appFolders = _appsSource?.Load();
    }

    /// <summary>
    /// Build the "sort by app first" panel from the scan's recognized APP zones (each app + the map of its
    /// data folders). Empty when the scan surfaced no apps (or on a resume with no zones). The keep/drop
    /// suggestion is the zone's own colour: rule-based, never the AI.
    /// </summary>
    private void BuildAppGroups(IReadOnlyList<RecognizedZoneRow> appZones)
    {
        AppGroups.Clear();
        foreach (var group in AppReviewGrouping.Build(appZones))
        {
            AppGroups.Add(group);
        }

        AppSortSummary = AppGroups.Count == 0
            ? ""
            : $"{AppGroups.Count} app(s) with data found on your PC. Keep or drop an app's data as a block here, then sort the rest of the folders below.";
        SyncAppGroups();
        Raise(nameof(HasAppGroups));
        Raise(nameof(CanProposeAi));
        ProposeAppWithAiCommand.RaiseCanExecuteChanged();
        ProposeAllAppsWithAiCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Mirror each app location's dropped state from the trash, so the panel matches every drop/restore.</summary>
    private void SyncAppGroups()
    {
        foreach (var group in AppGroups)
        {
            foreach (var location in group.Locations)
            {
                location.IsDropped = Trash.IsDropped(location.FullPath);
            }
        }
    }

    private async Task DropLocationAsync(AppReviewLocationViewModel location)
    {
        Trash.Drop(location.FullPath, await SizeOfAsync(location.FullPath));
        AfterPanelChange();
    }

    private void KeepLocation(AppReviewLocationViewModel location)
    {
        Trash.Restore(location.FullPath);
        AfterPanelChange();
    }

    /// <summary>Keep or drop a whole app's locations at once (drop = all, or only the rule-suggested ones).</summary>
    private async Task BulkAppAsync(AppReviewGroupViewModel group, bool drop, bool suggestedOnly)
    {
        foreach (var location in group.Locations)
        {
            if (!drop)
            {
                Trash.Restore(location.FullPath);
            }
            else if (!suggestedOnly || location.SuggestDrop)
            {
                Trash.Drop(location.FullPath, await SizeOfAsync(location.FullPath));
            }
        }

        AfterPanelChange();
    }

    /// <summary>
    /// The recursive size of a folder, on demand (read-only): the app zones carry paths but no sizes, so a
    /// drop measures the folder right then to get the trash total right. Best-effort: an unreadable or gone
    /// folder counts as 0 (it still drops; the exclusion is by path, not size).
    /// </summary>
    private async Task<long> SizeOfAsync(string path)
    {
        try
        {
            var children = await _browser.ListAsync(path, CancellationToken.None);
            return children.Sum(child => child.Bytes);
        }
        catch
        {
            return 0;
        }
    }

    private void SetStep(ReviewStep step)
    {
        _step = step;
        Raise(nameof(IsAppsStep));
        Raise(nameof(IsFilesStep));
    }

    /// <summary>Per-app "propose with AI" click: run it if the assistant is set up, else route to Settings.</summary>
    private void ProposeAppOrRedirect(AppReviewGroupViewModel group)
    {
        if (Ai is null || !Ai.IsEnabled)
        {
            AiSetupRequested?.Invoke();
            return;
        }

        _ = ProposeAppWithAiAsync(group);
    }

    /// <summary>"Propose all apps with AI" click: run it if the assistant is set up, else route to Settings.</summary>
    private void ProposeAllOrRedirect()
    {
        if (Ai is null || !Ai.IsEnabled)
        {
            AiSetupRequested?.Invoke();
            return;
        }

        _ = ProposeAllAppsWithAiAsync();
    }

    /// <summary>A wizard AI button (analyze folder / analyze all / colour files): run it if the assistant is
    /// set up, else route to Settings, so the buttons can stay visible instead of vanishing when it is off.</summary>
    private void RunAiOrRedirect(Func<Task> analysis)
    {
        if (Ai is null || !Ai.IsEnabled)
        {
            AiSetupRequested?.Invoke();
            return;
        }

        _ = analysis();
    }

    /// <summary>Ask the AI to refine ONE app's keep/drop suggestions (its locations only).</summary>
    public Task ProposeAppWithAiAsync(AppReviewGroupViewModel group) =>
        ProposeWithAiAsync([.. group.Locations], group.AppName);

    /// <summary>Ask the AI to refine the suggestions of EVERY app in the panel, in one pass.</summary>
    public Task ProposeAllAppsWithAiAsync() =>
        ProposeWithAiAsync([.. AppGroups.SelectMany(group => group.Locations)], "all apps");

    /// <summary>
    /// Refine each location's keep/drop colour with the AI (metadata only, like the rest of the assistant):
    /// list the folder's children, ask the endpoint, and recolour the row with the model's verdict and its
    /// own explanation. Progress + Cancel; two failures in a row stop the pass. Nothing is dropped: this
    /// only changes the SUGGESTION, so the user still decides (a stronger "Drop suggested" comes from here).
    /// </summary>
    private async Task ProposeWithAiAsync(IReadOnlyList<AppReviewLocationViewModel> locations, string what)
    {
        if (Ai is null || !Ai.IsEnabled || IsProposingAi || locations.Count == 0)
        {
            return;
        }

        Error = null;
        IsProposingAi = true;
        _aiProposeCancellation = new CancellationTokenSource();
        var token = _aiProposeCancellation.Token;
        var consecutiveFailures = 0;
        try
        {
            for (var i = 0; i < locations.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var location = locations[i];
                AiProposeBusyText = $"Asking the AI about {what}: {i + 1} of {locations.Count} ({location.Name})...";
                try
                {
                    var children = await _browser.ListAsync(location.FullPath, token);
                    var listing = children.Select(child => (child.Name, child.IsDirectory, child.Bytes)).ToList();
                    var suggestion = await Ai.SuggestForFolderAsync(location.FullPath, listing, token);
                    if (suggestion is null)
                    {
                        break; // the assistant was turned off mid-pass
                    }

                    var key = AiAssistantViewModel.ImportanceKeyOf(suggestion);
                    if (key.Length > 0)
                    {
                        location.SuggestKey = key;
                        location.SuggestReason = string.IsNullOrWhiteSpace(suggestion.Explanation)
                            ? "AI suggestion (no explanation given)."
                            : "AI: " + suggestion.Explanation.Trim();
                    }

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
                        Error = "The endpoint keeps failing, so the AI proposal stopped. Test the connection, then try again.";
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // cancelled; the suggestions refined so far are kept
        }
        finally
        {
            IsProposingAi = false;
            AiProposeBusyText = "";
            _aiProposeCancellation?.Dispose();
            _aiProposeCancellation = null;
        }
    }

    /// <summary>
    /// Shared refresh after an app-panel keep/drop: sync the panel and the trash, the summary, and the
    /// wizard's remaining count, persist the decision, and move the wizard off a folder it just dropped.
    /// </summary>
    private void AfterPanelChange()
    {
        RefreshTrash(); // also mirrors the drop into the app panel (SyncAppGroups)
        RaiseSummary();
        RaiseNav();
        TrashChanged?.Invoke();
        _ = MoveOffDroppedCurrentAsync();
    }

    /// <summary>
    /// If a per-app drop just trashed the folder the wizard is standing on, move to the nearest folder
    /// still under review (or clear the step when none is left, so the bar offers "Back up →").
    /// </summary>
    private async Task MoveOffDroppedCurrentAsync()
    {
        if (_folderIndex < 0 || _folderIndex >= _roots.Count || !Trash.IsDropped(_roots[_folderIndex].FullPath))
        {
            return;
        }

        if ((NextActiveIndex(_folderIndex) ?? PrevActiveIndex(_folderIndex)) is int target)
        {
            await GoToFolderAsync(target);
        }
        else
        {
            _folderIndex = -1;
            _trail.Clear();
            _current = [];
            Entries.Clear();
            UpdateStepText();
            FolderNote = "Every folder here is in the trash. Open the trash to restore one, or back up.";
            RaiseSummary();
            RaiseNav();
        }
    }

    /// <summary>The next review folder still kept (not trashed) after <paramref name="from"/>, or null.</summary>
    private int? NextActiveIndex(int from)
    {
        for (var i = from + 1; i < _roots.Count; i++)
        {
            if (!Trash.IsDropped(_roots[i].FullPath))
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>The previous review folder still kept (not trashed) before <paramref name="from"/>, or null.</summary>
    private int? PrevActiveIndex(int from)
    {
        for (var i = from - 1; i >= 0; i--)
        {
            if (!Trash.IsDropped(_roots[i].FullPath))
            {
                return i;
            }
        }

        return null;
    }

    private List<int> ActiveRootIndices()
    {
        var active = new List<int>();
        for (var i = 0; i < _roots.Count; i++)
        {
            if (!Trash.IsDropped(_roots[i].FullPath))
            {
                active.Add(i);
            }
        }

        return active;
    }

    /// <summary>"Folder X of N" counting only folders still under review (dropped ones no longer count).</summary>
    private void UpdateStepText()
    {
        if (_folderIndex < 0)
        {
            StepText = "";
            return;
        }

        var active = ActiveRootIndices();
        if (active.Count == 0)
        {
            StepText = "";
            return;
        }

        var position = active.IndexOf(_folderIndex);
        StepText = $"Folder {(position < 0 ? active.Count : position + 1)} of {active.Count}";
    }

    private void RaiseSummary()
    {
        Raise(nameof(KeptSummary));
        Raise(nameof(TrashButtonText));
    }

    private void RaiseNav()
    {
        Raise(nameof(HasFolder));
        Raise(nameof(HasPrevious));
        Raise(nameof(HasNext));
        Raise(nameof(OnLastFolder));
        Raise(nameof(ShowBackUp));
        Raise(nameof(AtFolderRoot));
        UpCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        PreviousCommand.RaiseCanExecuteChanged();
        NextCommand.RaiseCanExecuteChanged();
        DropFolderCommand.RaiseCanExecuteChanged();
        AnalyzeFolderCommand.RaiseCanExecuteChanged();
        AnalyzeAllCommand.RaiseCanExecuteChanged();
        AnalyzeFilesCommand.RaiseCanExecuteChanged();
        OpenCommand.RaiseCanExecuteChanged();
        DropCommand.RaiseCanExecuteChanged();
        DropLocationCommand.RaiseCanExecuteChanged();
        DropAppCommand.RaiseCanExecuteChanged();
        DropSuggestedCommand.RaiseCanExecuteChanged();
        Raise(nameof(CanProposeAi));
        ProposeAppWithAiCommand.RaiseCanExecuteChanged();
        ProposeAllAppsWithAiCommand.RaiseCanExecuteChanged();
        UpdateStepText(); // the remaining-folder count can change when a folder is dropped from the panel
    }
}
