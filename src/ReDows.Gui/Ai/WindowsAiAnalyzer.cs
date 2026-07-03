using ReDows.Core.Ai;
using ReDows.Providers.Windows.Ai;

namespace ReDows.Gui.Ai;

/// <summary>
/// The real analyzer: one OpenAI-compatible client per endpoint (URL + key + model), kept while the
/// endpoint doesn't change (so a discovered model id is reused between analyses). The key only ever
/// lives inside the client's auth header — never on disk. UI-thread use only.
/// </summary>
public sealed class WindowsAiAnalyzer : IAiAnalyzer
{
    private OpenAiCompatibleClient? _client;
    private AiEndpoint? _clientEndpoint;

    public Task<string> TestAsync(AiEndpoint endpoint, CancellationToken cancellationToken) =>
        ClientFor(endpoint).TestConnectionAsync(cancellationToken);

    public Task<AiSuggestion> AnalyzeAsync(AiEndpoint endpoint, FolderMetadata folder, CancellationToken cancellationToken) =>
        ClientFor(endpoint).AnalyzeAsync(folder, cancellationToken);

    private OpenAiCompatibleClient ClientFor(AiEndpoint endpoint)
    {
        var normalized = endpoint with { BaseUrl = OpenAiCompatibleClient.NormalizeBaseUrl(endpoint.BaseUrl) };
        if (_client is null || _clientEndpoint != normalized)
        {
            _client?.Dispose();
            _client = new OpenAiCompatibleClient(endpoint.BaseUrl, endpoint.ApiKey, model: endpoint.Model);
            _clientEndpoint = normalized;
        }

        return _client;
    }
}
