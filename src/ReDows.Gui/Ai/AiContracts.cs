using ReDows.Core.Ai;

namespace ReDows.Gui.Ai;

/// <summary>
/// Where the assistant talks to: the base URL, an OPTIONAL key (cloud services: held in memory only,
/// never written to disk), an OPTIONAL explicit model id (cloud services list hundreds of models, so
/// picking one is required there; a local server just uses whatever it has loaded), and an OPTIONAL
/// max-tokens cap. A null <paramref name="MaxTokens"/> means NO cap. Right for a self-hosted model
/// (think as long as it needs, it's your own machine); a paid API/subscription sets a cap to bound cost.
/// </summary>
public sealed record AiEndpoint(string BaseUrl, string? ApiKey, string? Model, int? MaxTokens = null);

/// <summary>
/// Analyzes one folder's whitelisted metadata against the configured endpoint. A seam: the real
/// implementation drives the OpenAI-compatible client (a local LM Studio / Ollama by default);
/// a test swaps a fake to exercise the view-model without any network.
/// </summary>
public interface IAiAnalyzer
{
    /// <summary>Prove the endpoint answers; returns the model id analyses will use.</summary>
    Task<string> TestAsync(AiEndpoint endpoint, CancellationToken cancellationToken);

    Task<AiSuggestion> AnalyzeAsync(AiEndpoint endpoint, FolderMetadata folder, CancellationToken cancellationToken);

    Task<AiSuggestion> AnalyzeFileAsync(AiEndpoint endpoint, FileInContext file, CancellationToken cancellationToken);

    /// <summary>Ask, in plain language, how to keep the useful data behind machine-bound (DPAPI) files before a reset.</summary>
    Task<string> AdviseAsync(AiEndpoint endpoint, IReadOnlyList<LockedFilesGroup> groups, CancellationToken cancellationToken);
}

/// <summary>
/// The AI assistant's settings: off by default, the endpoint URL (a LOCAL one by default), the optional
/// model id, and which KIND of connection the user picked ("local" self-hosted / "api" external key /
/// "proxy" external subscription) so the card reopens in the right mode. The API key is deliberately NOT
/// here. It is never persisted (invariant #5): the user re-enters it after a restart, like the vault
/// password. A null <paramref name="Connection"/> (old settings files) is read as "local". <paramref
/// name="MaxTokens"/> is the reply cap the user set for a paid API/subscription (ignored: unlimited
/// when self-hosted); null falls back to a sensible default.
/// </summary>
public sealed record AiSettings(bool Enabled, string BaseUrl, string? Model = null, string? Connection = null, int? MaxTokens = null);

/// <summary>
/// One AI "safe to drop" suggestion the user ACCEPTED. Remembered so the next scan pre-trashes the
/// same folder (visible and restorable, never silently ignored). Path and size only, nothing secret.
/// </summary>
public sealed record LearnedDrop(string Path, long Bytes);

/// <summary>
/// Persists the accepted-drop lessons between scans. Best-effort like the other stores: missing or
/// unreadable = nothing learned yet, and a failed save never breaks the app.
/// </summary>
public interface IAiLearnedStore
{
    IReadOnlyList<LearnedDrop> Load();

    void Save(IReadOnlyList<LearnedDrop> drops);
}

/// <summary>
/// Persists the AI settings between launches. Best-effort like the session store: a missing or
/// unreadable file just means defaults (disabled), and a failed save never breaks the app.
/// Holds NO secret: just an on/off flag and a URL.
/// </summary>
public interface IAiSettingsStore
{
    AiSettings? Load();

    void Save(AiSettings settings);
}
