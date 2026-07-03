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
    private CancellationTokenSource? _cancellation;

    private bool _isEnabled;
    private string _baseUrl = DefaultBaseUrl;
    private string _model = "";
    private string? _apiKey; // IN MEMORY ONLY — never persisted (invariant #5); re-entered after a restart
    private bool _isBusy;
    private string? _connectionStatus;
    private AiSuggestion? _result;
    private string? _resultFolderPath;
    private string? _error;

    public AiAssistantViewModel(IAiAnalyzer analyzer, IAiSettingsStore store)
    {
        _analyzer = analyzer;
        _store = store;
        if (_store.Load() is { } saved)
        {
            _isEnabled = saved.Enabled;
            _baseUrl = string.IsNullOrWhiteSpace(saved.BaseUrl) ? DefaultBaseUrl : saved.BaseUrl;
            _model = saved.Model ?? "";
        }

        TestCommand = new RelayCommand(async _ => await TestAsync(), _ => !IsBusy);
        CancelCommand = new RelayCommand(_ => _cancellation?.Cancel(), _ => IsBusy);
        AcceptCommand = new RelayCommand(_ => Accept());
        DismissCommand = new RelayCommand(_ => ClearResult());
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

    /// <summary>The last "test connection" outcome (model found / failure), shown next to the button.</summary>
    public string? ConnectionStatus
    {
        get => _connectionStatus;
        private set => Set(ref _connectionStatus, value);
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
        IsBusy = true;
        var run = new CancellationTokenSource();
        _cancellation = run;
        try
        {
            var metadata = AiPayload.Build(folderPath, children);
            var suggestion = await _analyzer.AnalyzeAsync(Endpoint, metadata, run.Token);
            if (!run.IsCancellationRequested) // navigation cleared this run while in flight → stale, discard
            {
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
    /// Forget the current suggestion AND cancel any analysis still in flight — called on navigation,
    /// so a slow reply can never show up (or act) over a folder it wasn't computed for.
    /// </summary>
    public void ClearResult()
    {
        _cancellation?.Cancel();
        Result = null;
        _resultFolderPath = null;
        Error = null;
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
            DropRequested?.Invoke(analyzedFolder);
        }

        ClearResult();
    }

    private void Persist() => _store.Save(new AiSettings(_isEnabled, _baseUrl, string.IsNullOrWhiteSpace(_model) ? null : _model.Trim())); // never the key
}
