using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ReDows.Core.Ai;

namespace ReDows.Providers.Windows.Ai;

/// <summary>
/// Talks to any OpenAI-compatible chat endpoint: a LOCAL LM Studio (http://localhost:1234) or Ollama
/// (http://localhost:11434) by default, so nothing leaves the PC; the same client covers a cloud API or
/// a user-run gateway later, since they all speak the same protocol (a base URL + an optional key).
/// Sends ONLY the whitelisted <see cref="FolderMetadata"/> rendered by <see cref="AiPayload"/>.
/// The HTTP handler is injectable so tests can capture the exact request without a network.
/// </summary>
public sealed class OpenAiCompatibleClient : IAiProvider, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly bool _modelIsExplicit;
    private readonly int? _maxTokens;
    private string? _model;

    public OpenAiCompatibleClient(string baseUrl, string? apiKey = null, HttpMessageHandler? handler = null, string? model = null, int? maxTokens = null)
    {
        _baseUrl = NormalizeBaseUrl(baseUrl);
        _model = string.IsNullOrWhiteSpace(model) ? null : model.Trim(); // explicit model → no discovery needed
        _modelIsExplicit = _model is not null;
        _maxTokens = maxTokens is > 0 ? maxTokens : null; // null = no cap (let a local model think as long as it needs)
        // No auto-redirect: the payload (whitelisted metadata) only ever goes to the URL the user set.
        _http = handler is null ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) : new HttpClient(handler);
        // NO wall-clock timeout on the HTTP client: a reasoning model on CPU can think for many minutes, and
        // any fixed cap would eventually cut a slow-but-working reply off mid-thought. The user stays in
        // control instead: the Cancel button and navigation cancel the request through its token, and
        // TestConnection sets its own quick 10-second bound via a linked token, so it never hangs.
        _http.Timeout = Timeout.InfiniteTimeSpan;
        if (!string.IsNullOrEmpty(apiKey))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
    }

    /// <summary>
    /// A bare host ("http://localhost:1234") gets the OpenAI-style "/v1" appended; a URL that already
    /// carries a path is kept as the user wrote it (some cloud services use their own base path, e.g.
    /// a "/v1beta/openai" compatibility root, where appending "/v1" would break it).
    /// </summary>
    public static string NormalizeBaseUrl(string baseUrl)
    {
        var trimmed = baseUrl.Trim().TrimEnd('/');
        var schemeEnd = trimmed.IndexOf("://", StringComparison.Ordinal);
        var afterScheme = schemeEnd < 0 ? trimmed : trimmed[(schemeEnd + 3)..];
        return afterScheme.Contains('/') ? trimmed : trimmed + "/v1";
    }

    /// <summary>
    /// Prove the endpoint answers (and, with a key, that it accepts it): list its models. With an
    /// explicit model configured, that model is what analyses use and what is reported; otherwise the
    /// first listed one is adopted and refreshed here, so swapping the loaded model mid-session recovers.
    /// </summary>
    public async Task<string> TestConnectionAsync(CancellationToken cancellationToken)
    {
        using var quick = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        quick.CancelAfter(TimeSpan.FromSeconds(10));
        var discovered = await FirstModelAsync(quick.Token);
        if (_modelIsExplicit)
        {
            return _model!;
        }

        _model = discovered;
        return discovered ?? throw new InvalidOperationException("The endpoint answered but reports no loaded model.");
    }

    public Task<AiSuggestion> AnalyzeAsync(FolderMetadata folder, CancellationToken cancellationToken) =>
        CompleteAsync(AiPayload.SystemPrompt, AiPayload.RenderPrompt(folder), cancellationToken);

    public Task<AiSuggestion> AnalyzeFileAsync(FileInContext file, CancellationToken cancellationToken) =>
        CompleteAsync(AiPayload.FileSystemPrompt, AiPayload.RenderFilePrompt(file), cancellationToken);

    /// <summary>
    /// Ask, in plain language, how to keep the useful data behind machine-bound (DPAPI) files before a
    /// reset. Sends the file names/paths only (never contents) and returns the model's free-text answer.
    /// </summary>
    public Task<string> AdviseAsync(IReadOnlyList<LockedFilesGroup> groups, CancellationToken cancellationToken) =>
        CompleteTextAsync(AiPayload.AdviceSystemPrompt, AiPayload.RenderAdvicePrompt(groups), cancellationToken);

    private async Task<AiSuggestion> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken)
    {
        var reply = await CompleteTextAsync(systemPrompt, userPrompt, cancellationToken);
        return AiPayload.ParseSuggestion(reply)
            ?? throw new InvalidOperationException("The model's reply could not be read as a suggestion.");
    }

    private async Task<string> CompleteTextAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken)
    {
        _model ??= await FirstModelAsync(cancellationToken)
            ?? throw new InvalidOperationException("No model is loaded on the endpoint.");

        // Build the request with an OPTIONAL cap. A "reasoning" model (Qwen3, DeepSeek-R1...) thinks for
        // hundreds of tokens BEFORE its answer, so a tight cap truncated it mid-thought and left an empty
        // reply. Self-hosted → no cap at all (think as long as needed, it's your own machine); a paid
        // API/subscription → the user's chosen cap so a runaway reply can't rack up cost.
        var request = new Dictionary<string, object?>
        {
            ["model"] = _model,
            ["temperature"] = 0.2,
            ["stream"] = false,
            ["messages"] = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt },
            },
        };
        if (_maxTokens is int cap)
        {
            request["max_tokens"] = cap;
        }

        var payload = JsonSerializer.Serialize(request);

        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync($"{_baseUrl}/chat/completions", content, cancellationToken);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return ExtractMessageText(body)
            ?? throw new InvalidOperationException("The endpoint's reply had no message content.");
    }

    private async Task<string?> FirstModelAsync(CancellationToken cancellationToken)
    {
        var body = await _http.GetStringAsync($"{_baseUrl}/models", cancellationToken);
        using var json = JsonDocument.Parse(body);
        if (json.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in data.EnumerateArray())
            {
                if (entry.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } model)
                {
                    return model;
                }
            }
        }

        return null;
    }

    private static string? ExtractMessageText(string responseBody)
    {
        try
        {
            using var json = JsonDocument.Parse(responseBody);
            if (!json.RootElement.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0
                || !choices[0].TryGetProperty("message", out var message))
            {
                return null;
            }

            // Normal case: the answer is in "content". A reasoning model may leave "content" empty and
            // put everything (thinking + the JSON) in "reasoning_content". Fall back to that so its
            // reply is still readable (ParseSuggestion then digs the JSON out of the thinking text).
            if (message.TryGetProperty("content", out var content)
                && content.GetString() is { } text && !string.IsNullOrWhiteSpace(text))
            {
                return text;
            }

            return message.TryGetProperty("reasoning_content", out var reasoning)
                ? reasoning.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
