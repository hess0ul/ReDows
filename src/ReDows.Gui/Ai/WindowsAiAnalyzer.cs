using ReDows.Core.Ai;
using ReDows.Providers.Windows.Ai;

namespace ReDows.Gui.Ai;

/// <summary>
/// The real analyzer: one OpenAI-compatible client per endpoint URL, kept while the URL doesn't change
/// (so the discovered model id is reused between analyses). UI-thread use only.
/// </summary>
public sealed class WindowsAiAnalyzer : IAiAnalyzer
{
    private OpenAiCompatibleClient? _client;
    private string? _clientUrl;

    public Task<string> TestAsync(string baseUrl, CancellationToken cancellationToken) =>
        ClientFor(baseUrl).TestConnectionAsync(cancellationToken);

    public Task<AiSuggestion> AnalyzeAsync(string baseUrl, FolderMetadata folder, CancellationToken cancellationToken) =>
        ClientFor(baseUrl).AnalyzeAsync(folder, cancellationToken);

    private OpenAiCompatibleClient ClientFor(string baseUrl)
    {
        var normalized = OpenAiCompatibleClient.NormalizeBaseUrl(baseUrl);
        if (_client is null || !string.Equals(_clientUrl, normalized, StringComparison.OrdinalIgnoreCase))
        {
            _client?.Dispose();
            _client = new OpenAiCompatibleClient(baseUrl);
            _clientUrl = normalized;
        }

        return _client;
    }
}
