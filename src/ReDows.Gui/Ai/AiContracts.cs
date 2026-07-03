using ReDows.Core.Ai;

namespace ReDows.Gui.Ai;

/// <summary>
/// Analyzes one folder's whitelisted metadata against the configured endpoint. A seam: the real
/// implementation drives the OpenAI-compatible client (a local LM Studio / Ollama by default);
/// a test swaps a fake to exercise the view-model without any network.
/// </summary>
public interface IAiAnalyzer
{
    /// <summary>Prove the endpoint answers; returns the model id analyses will use.</summary>
    Task<string> TestAsync(string baseUrl, CancellationToken cancellationToken);

    Task<AiSuggestion> AnalyzeAsync(string baseUrl, FolderMetadata folder, CancellationToken cancellationToken);
}

/// <summary>The AI assistant's settings: off by default, and the endpoint URL (a LOCAL one by default).</summary>
public sealed record AiSettings(bool Enabled, string BaseUrl);

/// <summary>
/// Persists the AI settings between launches. Best-effort like the session store — a missing or
/// unreadable file just means defaults (disabled), and a failed save never breaks the app.
/// Holds NO secret: just an on/off flag and a URL.
/// </summary>
public interface IAiSettingsStore
{
    AiSettings? Load();

    void Save(AiSettings settings);
}
