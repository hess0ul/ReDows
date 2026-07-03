using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ReDows.Core.Ai;

namespace ReDows.Providers.Windows.Ai;

/// <summary>
/// Talks to any OpenAI-compatible chat endpoint — a LOCAL LM Studio (http://localhost:1234) or Ollama
/// (http://localhost:11434) by default, so nothing leaves the PC; the same client covers a cloud API or
/// a user-run gateway later, since they all speak the same protocol (a base URL + an optional key).
/// Sends ONLY the whitelisted <see cref="FolderMetadata"/> rendered by <see cref="AiPayload"/>.
/// The HTTP handler is injectable so tests can capture the exact request without a network.
/// </summary>
public sealed class OpenAiCompatibleClient : IAiProvider, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private string? _model;

    public OpenAiCompatibleClient(string baseUrl, string? apiKey = null, HttpMessageHandler? handler = null)
    {
        _baseUrl = NormalizeBaseUrl(baseUrl);
        // No auto-redirect: the payload (whitelisted metadata) only ever goes to the URL the user set.
        _http = handler is null ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(120); // a small local model on CPU can be slow
        if (!string.IsNullOrEmpty(apiKey))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
    }

    /// <summary>
    /// "http://localhost:1234" and "http://localhost:1234/v1" both become ".../v1" — the OpenAI-style root.
    /// </summary>
    public static string NormalizeBaseUrl(string baseUrl)
    {
        var trimmed = baseUrl.Trim().TrimEnd('/');
        return trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? trimmed : trimmed + "/v1";
    }

    /// <summary>Prove the endpoint answers: list its models and return the first id (the one analyses will use).</summary>
    public async Task<string> TestConnectionAsync(CancellationToken cancellationToken)
    {
        using var quick = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        quick.CancelAfter(TimeSpan.FromSeconds(10));
        var model = await FirstModelAsync(quick.Token);
        _model = model; // keep analyses in step when the user swapped the loaded model mid-session
        return model ?? throw new InvalidOperationException("The endpoint answered but reports no loaded model.");
    }

    public async Task<AiSuggestion> AnalyzeAsync(FolderMetadata folder, CancellationToken cancellationToken)
    {
        _model ??= await FirstModelAsync(cancellationToken)
            ?? throw new InvalidOperationException("No model is loaded on the endpoint.");

        var payload = JsonSerializer.Serialize(new
        {
            model = _model,
            temperature = 0.2,
            max_tokens = 500,
            stream = false,
            messages = new object[]
            {
                new { role = "system", content = AiPayload.SystemPrompt },
                new { role = "user", content = AiPayload.RenderPrompt(folder) },
            },
        });

        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync($"{_baseUrl}/chat/completions", content, cancellationToken);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var reply = ExtractMessageText(body)
            ?? throw new InvalidOperationException("The endpoint's reply had no message content.");
        return AiPayload.ParseSuggestion(reply)
            ?? throw new InvalidOperationException("The model's reply could not be read as a suggestion.");
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
            return json.RootElement.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("message", out var message)
                && message.TryGetProperty("content", out var text)
                    ? text.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
