using ReDows.Core.Ai;
using ReDows.Gui.Ai;
using ReDows.Gui.Navigation;

namespace ReDows.Gui.ViewModels;

/// <summary>
/// The optional AI assistant of the Review screen. OFF by default; points at a LOCAL endpoint
/// (LM Studio) unless the user changes the URL — so by default nothing ever leaves the PC.
/// It sends ONLY the whitelisted folder metadata (names/sizes, built by <see cref="AiPayload"/>),
/// and the reply is a SUGGESTION the user accepts or dismisses — the assistant never decides.
/// All state is plain and testable off a fake <see cref="IAiAnalyzer"/>.
/// </summary>
public sealed class AiAssistantViewModel : ViewModelBase
{
    /// <summary>LM Studio's local server — the default endpoint, on this PC (Ollama: port 11434).</summary>
    public const string DefaultBaseUrl = "http://localhost:1234";

    private readonly IAiAnalyzer _analyzer;
    private readonly IAiSettingsStore _store;
    private readonly IAiLearnedStore? _learnedStore;
    private List<LearnedDrop>? _learned; // lazy-loaded lessons (accepted drops remembered across scans)
    private readonly Dictionary<string, AiSuggestion> _byFolder = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cancellation;      // one folder's analysis — cancelled by navigation
    private CancellationTokenSource? _batchCancellation; // "analyze all" — survives navigation, Cancel stops it

    private bool _isEnabled;
    private string _baseUrl = DefaultBaseUrl;
    private string _model = "";
    private string? _apiKey; // IN MEMORY ONLY — never persisted (invariant #5); re-entered after a restart
    private bool _isBusy;
    private string _busyText = "";
    private string? _connectionStatus;
    private string? _batchStatus;
    private AiSuggestion? _result;
    private string? _resultFolderPath;
    private string? _error;

    public AiAssistantViewModel(IAiAnalyzer analyzer, IAiSettingsStore store, IAiLearnedStore? learnedStore = null)
    {
        _analyzer = analyzer;
        _store = store;
        _learnedStore = learnedStore;
        if (_store.Load() is { } saved)
        {
            _isEnabled = saved.Enabled;
            _baseUrl = string.IsNullOrWhiteSpace(saved.BaseUrl) ? DefaultBaseUrl : saved.BaseUrl;
            _model = saved.Model ?? "";
        }

        TestCommand = new RelayCommand(async _ => await TestAsync(), _ => !IsBusy);
        CancelCommand = new RelayCommand(_ => { _cancellation?.Cancel(); _batchCancellation?.Cancel(); }, _ => IsBusy);
        AcceptCommand = new RelayCommand(_ => Accept());
        DismissCommand = new RelayCommand(_ => Dismiss());
    }

    /// <summary>
    /// Raised when the user accepts a "safe to drop" suggestion, carrying the PATH the suggestion was
    /// computed for — Review only acts if that folder is still the one on screen (a slow analysis must
    /// never drop a folder the model didn't look at).
    /// </summary>
    public event Action<string>? DropRequested;

    public RelayCommand TestCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand AcceptCommand { get; }

    public RelayCommand DismissCommand { get; }

    public bool IsEnabled
    {
        get => _isEnabled;
        set { Set(ref _isEnabled, value); Persist(); }
    }

    public string BaseUrl
    {
        get => _baseUrl;
        set { Set(ref _baseUrl, value); Persist(); }
    }

    /// <summary>
    /// Optional explicit model id — required for a cloud service (they list hundreds); a local server
    /// just uses its loaded model when left empty. Persisted (it is not a secret).
    /// </summary>
    public string Model
    {
        get => _model;
        set { Set(ref _model, value); Persist(); }
    }

    /// <summary>
    /// Take the API key from the view's PasswordBox, for THIS session only: it lives in memory, goes
    /// into the request's auth header, and is never written to disk (invariant #5 — like the vault
    /// password, it is re-entered after a restart).
    /// </summary>
    public void SetApiKey(string? key) => _apiKey = string.IsNullOrWhiteSpace(key) ? null : key;

    private AiEndpoint Endpoint => new(BaseUrl, _apiKey, string.IsNullOrWhiteSpace(_model) ? null : _model.Trim());

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            Set(ref _isBusy, value);
            TestCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>What the assistant is doing right now, shown while busy (single or batch analysis).</summary>
    public string BusyText
    {
        get => _busyText;
        private set => Set(ref _busyText, value);
    }

    /// <summary>The last "test connection" outcome (model found / failure), shown next to the button.</summary>
    public string? ConnectionStatus
    {
        get => _connectionStatus;
        private set => Set(ref _connectionStatus, value);
    }

    /// <summary>After "analyze all": how many folders got a suggestion, split by kind (and failures).</summary>
    public string? BatchStatus
    {
        get => _batchStatus;
        private set => Set(ref _batchStatus, value);
    }

    public AiSuggestion? Result
    {
        get => _result;
        private set
        {
            Set(ref _result, value);
            Raise(nameof(HasResult));
            Raise(nameof(ResultTitle));
            Raise(nameof(CanAcceptDrop));
        }
    }

    public bool HasResult => Result is not null;

    /// <summary>One line over the explanation: what the model suggests, and how sure it is.</summary>
    public string ResultTitle => Result switch
    {
        null => "",
        { Classification: AiSuggestion.Drop } r => $"Suggestion: safe to drop · confidence: {r.Confidence}",
        { Classification: AiSuggestion.Keep } r => $"Suggestion: keep (user data) · confidence: {r.Confidence}",
        { Classification: AiSuggestion.Mixed } r => $"Suggestion: mixed — look inside · confidence: {r.Confidence}",
        var r => $"Suggestion: unknown · confidence: {r.Confidence}",
    };

    /// <summary>Only a "safe to drop" suggestion is actionable — keep is already the default.</summary>
    public bool CanAcceptDrop => Result?.Classification == AiSuggestion.Drop;

    public string? Error
    {
        get => _error;
        private set { Set(ref _error, value); Raise(nameof(HasError)); }
    }

    public bool HasError => Error is not null;

    /// <summary>
    /// Analyze one folder from its ALREADY-LISTED children (Review's rows): builds the whitelisted
    /// metadata and asks the endpoint. Errors surface as a message, never a crash. The OFF state is
    /// enforced HERE, not just by a hidden button — a disabled assistant never talks to anything.
    /// A result that lands after the user navigated away (its run was cancelled) is discarded.
    /// </summary>
    public async Task AnalyzeAsync(string folderPath, IReadOnlyList<(string Name, bool IsDirectory, long Bytes)> children)
    {
        if (!IsEnabled || IsBusy)
        {
            return;
        }

        Error = null;
        Result = null;
        _resultFolderPath = null;
        BusyText = "Asking the AI about this folder…";
        IsBusy = true;
        var run = new CancellationTokenSource();
        _cancellation = run;
        try
        {
            var metadata = AiPayload.Build(folderPath, children);
            var suggestion = await _analyzer.AnalyzeAsync(Endpoint, metadata, run.Token);
            if (!run.IsCancellationRequested) // navigation cleared this run while in flight → stale, discard
            {
                _byFolder[folderPath] = suggestion; // remembered — navigating back re-shows it
                _resultFolderPath = folderPath;
                Result = suggestion;
            }
        }
        catch (OperationCanceledException) when (run.IsCancellationRequested)
        {
            // cancelled (user, or navigation) — no result, no error
        }
        catch (Exception ex)
        {
            // includes the HTTP timeout (a cancellation NOT ours) — surfaced, not silently swallowed
            Error = ex is OperationCanceledException ? "The endpoint took too long to answer (timed out)." : ex.Message;
        }
        finally
        {
            IsBusy = false;
            if (ReferenceEquals(_cancellation, run))
            {
                _cancellation = null;
            }

            run.Dispose();
        }
    }

    /// <summary>
    /// Forget the DISPLAYED suggestion and cancel a single analysis still in flight — called on
    /// navigation, so a slow reply can never show up (or act) over a folder it wasn't computed for.
    /// Stored batch suggestions and a running batch are NOT touched (the batch survives navigation).
    /// </summary>
    public void ClearResult()
    {
        _cancellation?.Cancel();
        Result = null;
        _resultFolderPath = null;
        Error = null;
    }

    /// <summary>
    /// Analyze EVERY review folder in sequence — the "analyze all" batch. Each suggestion is stored by
    /// folder path (the wizard shows it when you land on that folder); nothing is ever acted on without
    /// the user's per-folder Accept. One bad folder is counted and skipped; two consecutive failures
    /// stop the batch (the endpoint is probably down). Cancel keeps what was already analyzed.
    /// </summary>
    public async Task AnalyzeAllAsync(
        IReadOnlyList<(string Path, string Name)> folders,
        Func<string, CancellationToken, Task<IReadOnlyList<(string Name, bool IsDirectory, long Bytes)>>> listChildren)
    {
        if (!IsEnabled || IsBusy || folders.Count == 0)
        {
            return;
        }

        Error = null;
        BatchStatus = null;
        IsBusy = true;
        var run = new CancellationTokenSource();
        _batchCancellation = run;
        var failed = 0;
        var consecutiveFailures = 0;
        try
        {
            for (var i = 0; i < folders.Count; i++)
            {
                run.Token.ThrowIfCancellationRequested();
                var (path, name) = folders[i];
                BusyText = $"Analyzing folder {i + 1} of {folders.Count} — {name}…";
                try
                {
                    var children = await listChildren(path, run.Token);
                    var suggestion = await _analyzer.AnalyzeAsync(Endpoint, AiPayload.Build(path, children), run.Token);
                    _byFolder[path] = suggestion;
                    consecutiveFailures = 0;
                }
                catch (OperationCanceledException) when (run.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    failed++;
                    if (++consecutiveFailures >= 2)
                    {
                        Error = "The endpoint keeps failing — the batch stopped. Test the connection, then try again.";
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // cancelled — everything analyzed so far is kept
        }
        finally
        {
            IsBusy = false;
            if (ReferenceEquals(_batchCancellation, run))
            {
                _batchCancellation = null;
            }

            run.Dispose();
            BatchStatus = SummarizeStored(failed);
        }
    }

    /// <summary>Show the remembered suggestion for a folder the wizard just landed on (no new analysis).</summary>
    public void ShowStoredFor(string? folderPath)
    {
        if (folderPath is not null && _byFolder.TryGetValue(folderPath, out var suggestion))
        {
            _resultFolderPath = folderPath;
            Result = suggestion;
        }
    }

    /// <summary>Forget a folder's remembered suggestion (it was consumed, dismissed, or trashed).</summary>
    public void Forget(string folderPath) => _byFolder.Remove(folderPath);

    /// <summary>New review session (new scan): drop every remembered suggestion and the batch summary.</summary>
    public void Reset()
    {
        _byFolder.Clear();
        BatchStatus = null;
        ClearResult();
    }

    /// <summary>The remembered lessons: folders whose "safe to drop" suggestion the user once accepted.</summary>
    public IReadOnlyList<LearnedDrop> LearnedDrops => Lessons;

    /// <summary>
    /// Remember an ACCEPTED "safe to drop" — the next scan pre-trashes this folder (restorable).
    /// Only ever called on the user's Accept gesture; the model itself can't learn anything.
    /// </summary>
    public void Learn(string folderPath, long bytes)
    {
        var lessons = Lessons;
        lessons.RemoveAll(d => string.Equals(d.Path, folderPath, StringComparison.OrdinalIgnoreCase));
        lessons.Add(new LearnedDrop(folderPath, bytes));
        _learnedStore?.Save(lessons);
    }

    /// <summary>Forget a lesson — the user restored the folder from the trash ("I changed my mind").</summary>
    public void Unlearn(string folderPath)
    {
        var lessons = Lessons;
        if (lessons.RemoveAll(d => string.Equals(d.Path, folderPath, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            _learnedStore?.Save(lessons);
        }
    }

    private List<LearnedDrop> Lessons => _learned ??= _learnedStore?.Load().ToList() ?? [];

    private string SummarizeStored(int failed)
    {
        int drop = 0, keep = 0, mixed = 0, unknown = 0;
        foreach (var suggestion in _byFolder.Values)
        {
            switch (suggestion.Classification)
            {
                case AiSuggestion.Drop: drop++; break;
                case AiSuggestion.Keep: keep++; break;
                case AiSuggestion.Mixed: mixed++; break;
                default: unknown++; break;
            }
        }

        return $"Analyzed {_byFolder.Count} folder(s) — {drop} safe to drop · {keep} keep · {mixed} mixed · {unknown} unknown"
            + (failed > 0 ? $" · {failed} failed" : "");
    }

    /// <summary>Prove the endpoint answers (public like the other view-models' async entry points, for tests).</summary>
    public async Task TestAsync()
    {
        if (IsBusy)
        {
            return;
        }

        ConnectionStatus = "Testing…";
        IsBusy = true;
        var run = new CancellationTokenSource();
        _cancellation = run;
        try
        {
            var model = await _analyzer.TestAsync(Endpoint, run.Token);
            ConnectionStatus = $"OK — model: {model}";
        }
        catch (OperationCanceledException) when (run.IsCancellationRequested)
        {
            ConnectionStatus = "Cancelled.";
        }
        catch (Exception ex)
        {
            // includes the connection timeout (a cancellation NOT ours) — reported as a failure
            ConnectionStatus = ex is OperationCanceledException ? "Failed: no answer (timed out)." : $"Failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            if (ReferenceEquals(_cancellation, run))
            {
                _cancellation = null;
            }

            run.Dispose();
        }
    }

    private void Accept()
    {
        if (CanAcceptDrop && _resultFolderPath is { } analyzedFolder)
        {
            Forget(analyzedFolder); // consumed — must not re-appear when landing on it again
            DropRequested?.Invoke(analyzedFolder);
        }

        ClearResult();
    }

    private void Dismiss()
    {
        if (_resultFolderPath is { } shownFolder)
        {
            Forget(shownFolder); // dismissed — must not re-appear when landing on it again
        }

        ClearResult();
    }

    private void Persist() => _store.Save(new AiSettings(_isEnabled, _baseUrl, string.IsNullOrWhiteSpace(_model) ? null : _model.Trim())); // never the key
}
