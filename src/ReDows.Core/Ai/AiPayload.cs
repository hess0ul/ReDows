using System.Text;
using System.Text.Json;

namespace ReDows.Core.Ai;

/// <summary>
/// The pure heart of the AI assistant, and its privacy proof. <see cref="Build"/> turns an
/// already-listed directory into the whitelisted <see cref="FolderMetadata"/> (names and sizes only —
/// it takes listing results as input and cannot read a file). <see cref="RenderPrompt"/> turns that
/// metadata into the model prompt (so tests can assert exactly what would leave the machine).
/// <see cref="ParseSuggestion"/> reads the model's reply defensively — a rambling or broken reply
/// degrades to "unknown"/null, never to a crash or a fabricated verdict.
/// </summary>
public static class AiPayload
{
    /// <summary>Children beyond this many are summarized as a count (keeps the prompt bounded).</summary>
    public const int MaxChildren = 60;

    /// <summary>An explanation longer than this is truncated (keeps a hostile reply display-bounded).</summary>
    public const int MaxExplanationLength = 2000;

    private static readonly string[] ExecutableExtensions = [".exe", ".msi", ".bat", ".cmd"];

    /// <summary>
    /// Build the whitelisted metadata for one folder from its ALREADY-LISTED children
    /// (name / is-directory / size triples). Pure: no I/O, no file ever opened.
    /// </summary>
    public static FolderMetadata Build(string folderPath, IEnumerable<(string Name, bool IsDirectory, long Bytes)> children)
    {
        var all = children.ToList();
        var kept = all
            .OrderByDescending(c => c.Bytes)
            .Take(MaxChildren)
            .Select(c => new FolderFact(c.Name, c.IsDirectory, c.Bytes))
            .ToList();

        var normalized = folderPath.TrimEnd('/', '\\');
        var lastSeparator = normalized.LastIndexOfAny(['/', '\\']);
        return new FolderMetadata(
            FolderPath: normalized,
            FolderName: lastSeparator < 0 ? normalized : normalized[(lastSeparator + 1)..],
            TotalBytes: all.Sum(c => c.Bytes),
            HasExecutable: all.Any(c => !c.IsDirectory && ExecutableExtensions.Any(e => c.Name.EndsWith(e, StringComparison.OrdinalIgnoreCase))),
            Children: kept,
            ChildrenOmitted: Math.Max(0, all.Count - kept.Count));
    }

    /// <summary>
    /// The system message: what the assistant is for, and the strict JSON shape it must answer with.
    /// </summary>
    public const string SystemPrompt =
        "You help a user sort folders on a Windows PC before a factory reset. " +
        "From FOLDER METADATA ONLY (folder path and the names/sizes of its entries — you never see file contents), " +
        "identify what the folder most likely is, and whether it holds USER DATA to keep or a re-downloadable " +
        "application bundle / cache that is safe to drop. " +
        "Answer with STRICT JSON only, no prose around it: " +
        "{\"classification\":\"keep|drop|mixed|unknown\",\"explanation\":\"one short paragraph, plain language\",\"confidence\":\"high|medium|low\"}. " +
        "keep = user-created or user-configured data. drop = re-obtainable program files, caches, bundles. " +
        "mixed = both are present (say where the user data likely lives). " +
        "If you are not reasonably sure, use unknown with low confidence — never guess confidently.";

    /// <summary>Render the user message: the whitelisted metadata, nothing else.</summary>
    public static string RenderPrompt(FolderMetadata folder)
    {
        var text = new StringBuilder();
        text.AppendLine($"Folder: {folder.FolderPath}");
        text.AppendLine($"Name: {folder.FolderName}");
        text.AppendLine($"Total size: {folder.TotalBytes} bytes");
        text.AppendLine($"Contains an executable: {(folder.HasExecutable ? "yes" : "no")}");
        text.AppendLine($"Entries ({folder.Children.Count} listed{(folder.ChildrenOmitted > 0 ? $", {folder.ChildrenOmitted} more omitted" : "")}):");
        foreach (var child in folder.Children)
        {
            text.AppendLine($"  {(child.IsDirectory ? "dir " : "file")}  {child.Name}  {child.Bytes} bytes");
        }

        return text.ToString();
    }

    /// <summary>
    /// Parse the model's reply into a suggestion. Tolerates prose around the JSON (extracts the first
    /// {...} block); null when no JSON can be read at all; an unexpected classification or confidence
    /// degrades to "unknown"/"low" rather than inventing meaning.
    /// </summary>
    public static AiSuggestion? ParseSuggestion(string modelText)
    {
        if (string.IsNullOrWhiteSpace(modelText))
        {
            return null;
        }

        var start = modelText.IndexOf('{');
        var end = modelText.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            var dto = JsonSerializer.Deserialize<SuggestionDto>(
                modelText[start..(end + 1)],
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (dto is null)
            {
                return null;
            }

            var classification = dto.Classification?.Trim().ToLowerInvariant() switch
            {
                AiSuggestion.Keep => AiSuggestion.Keep,
                AiSuggestion.Drop => AiSuggestion.Drop,
                AiSuggestion.Mixed => AiSuggestion.Mixed,
                _ => AiSuggestion.Unknown,
            };
            var confidence = dto.Confidence?.Trim().ToLowerInvariant() switch
            {
                "high" => "high",
                "medium" => "medium",
                _ => "low",
            };
            var explanation = dto.Explanation?.Trim() ?? "";
            if (explanation.Length > MaxExplanationLength) // a hostile endpoint can't flood the UI
            {
                explanation = explanation[..MaxExplanationLength] + "…";
            }

            return new AiSuggestion(classification, explanation, confidence);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record SuggestionDto(string? Classification, string? Explanation, string? Confidence);
}
