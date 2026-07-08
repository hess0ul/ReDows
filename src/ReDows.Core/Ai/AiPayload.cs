using System.Text;
using System.Text.Json;

namespace ReDows.Core.Ai;

/// <summary>
/// The pure heart of the AI assistant, and its privacy proof. <see cref="Build"/> turns an
/// already-listed directory into the whitelisted <see cref="FolderMetadata"/> (names and sizes only:
/// it takes listing results as input and cannot read a file). <see cref="RenderPrompt"/> turns that
/// metadata into the model prompt (so tests can assert exactly what would leave the machine).
/// <see cref="ParseSuggestion"/> reads the model's reply defensively: a rambling or broken reply
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
        "From FOLDER METADATA ONLY (folder path and the names/sizes of its entries, never the file contents), " +
        "identify what the folder most likely is, and whether it holds USER DATA to keep or a re-downloadable " +
        "application bundle / cache that is safe to drop. " +
        "Answer with STRICT JSON only, no prose around it: " +
        "{\"classification\":\"keep|drop|mixed|unknown\",\"explanation\":\"one short paragraph, plain language\",\"confidence\":\"high|medium|low\"}. " +
        "keep = user-created or user-configured data. drop = re-obtainable program files, caches, bundles. " +
        "mixed = both are present (say where the user data likely lives). " +
        "The folder PATH is a strong signal: the same name can be a re-obtainable app in one place and personal " +
        "data in another, so weigh where it sits. A folder inside a personal location (Documents, a Backup or " +
        "Sauvegarde folder, a personal data drive) is likely data to KEEP even if its name resembles an app. " +
        "If you are not reasonably sure, use unknown with low confidence. Never guess confidently.";

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
    /// The system message for judging ONE entry in the context of its folder: same strict-JSON contract
    /// as the folder one, but told to weigh the surrounding folder (the tree) when deciding.
    /// </summary>
    public const string FileSystemPrompt =
        "You help a user sort files on a Windows PC before a factory reset. You are given ONE entry (a file " +
        "or subfolder) and, as CONTEXT, the folder it lives in and that folder's other entries (names and " +
        "sizes only, never the file contents). Weigh the entry's name/type/size TOGETHER WITH its " +
        "surrounding folder to judge whether it is USER DATA worth keeping or a re-downloadable / regenerated " +
        "file safe to drop. Answer with STRICT JSON only, no prose around it: " +
        "{\"classification\":\"keep|drop|mixed|unknown\",\"explanation\":\"one short sentence, plain language\",\"confidence\":\"high|medium|low\"}. " +
        "keep = user-created/edited or user-configured data. drop = a re-obtainable program file, cache, log, " +
        "temp, or a file the app recreates. mixed = it depends on what's inside. " +
        "Weigh WHERE it sits: the same name under a personal location (Documents, a Backup or Sauvegarde " +
        "folder, a personal data drive) leans keep, while under a drive root or an app-data path it leans drop. " +
        "If you are not reasonably sure, use unknown with low confidence. Never guess confidently.";

    /// <summary>
    /// Build the whitelisted per-entry payload: the target entry plus its already-built parent
    /// <see cref="FolderMetadata"/> (the folder tree) and an optional short context note. Pure, no I/O.
    /// The note is length-capped so a hostile prior verdict can't bloat the prompt.
    /// </summary>
    public static FileInContext BuildFileInContext(
        string entryPath, string entryName, bool isDirectory, long bytes, FolderMetadata parent, string? parentContext)
    {
        var note = string.IsNullOrWhiteSpace(parentContext)
            ? null
            : parentContext.Trim() is { Length: > MaxExplanationLength } tooLong
                ? tooLong[..MaxExplanationLength] + "..."
                : parentContext.Trim();
        return new FileInContext(entryPath, entryName, isDirectory, bytes, parent, note);
    }

    /// <summary>Render the per-entry user message: the entry, then its folder context (tree), nothing else.</summary>
    public static string RenderFilePrompt(FileInContext entry)
    {
        var text = new StringBuilder();
        text.AppendLine($"Entry to judge: {entry.EntryName}");
        text.AppendLine($"Type: {(entry.IsDirectory ? "folder" : "file")}");
        text.AppendLine($"Size: {entry.Bytes} bytes");
        text.AppendLine();
        text.AppendLine("It lives in this folder (context):");
        text.AppendLine($"Folder: {entry.Parent.FolderPath}");
        text.AppendLine($"Name: {entry.Parent.FolderName}");
        text.AppendLine($"Total size: {entry.Parent.TotalBytes} bytes");
        text.AppendLine($"Contains an executable: {(entry.Parent.HasExecutable ? "yes" : "no")}");
        if (entry.ParentContext is { Length: > 0 } note)
        {
            text.AppendLine($"Folder context: {note}");
        }

        text.AppendLine($"Other entries in this folder ({entry.Parent.Children.Count} listed{(entry.Parent.ChildrenOmitted > 0 ? $", {entry.Parent.ChildrenOmitted} more omitted" : "")}):");
        foreach (var sibling in entry.Parent.Children)
        {
            text.AppendLine($"  {(sibling.IsDirectory ? "dir " : "file")}  {sibling.Name}  {sibling.Bytes} bytes");
        }

        return text.ToString();
    }

    /// <summary>
    /// Parse the model's reply into a suggestion. Robust to a "reasoning" model that thinks out loud
    /// before answering: strips &lt;think&gt; blocks, then scans every balanced {...} object from LAST to
    /// first and returns the model's real verdict, so the schema example it echoes while thinking (which
    /// parses as "unknown") never wins over the actual answer that follows. Null when no JSON can be read;
    /// an unexpected classification or confidence degrades to "unknown"/"low" rather than inventing meaning.
    /// </summary>
    public static AiSuggestion? ParseSuggestion(string modelText)
    {
        if (string.IsNullOrWhiteSpace(modelText))
        {
            return null;
        }

        var text = StripThinkBlocks(modelText);
        AiSuggestion? fallback = null;
        foreach (var span in JsonObjectSpans(text).Reverse()) // the real answer is the LAST JSON object
        {
            if (TryReadSuggestion(span) is not { } parsed)
            {
                continue;
            }

            if (parsed.Classification != AiSuggestion.Unknown)
            {
                return parsed; // a real verdict (keep/drop/mixed) beats an "unknown" echo of the schema
            }

            fallback ??= parsed; // keep the last "unknown" in case there is no clearer verdict anywhere
        }

        return fallback;
    }

    private static AiSuggestion? TryReadSuggestion(string json)
    {
        try
        {
            var dto = JsonSerializer.Deserialize<SuggestionDto>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (dto is null || (dto.Classification is null && dto.Explanation is null && dto.Confidence is null))
            {
                return null; // a {...} with none of our fields is not a suggestion at all
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
                explanation = explanation[..MaxExplanationLength] + "...";
            }

            return new AiSuggestion(classification, explanation, confidence);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Remove &lt;think&gt;...&lt;/think&gt; / &lt;thinking&gt;...&lt;/thinking&gt; blocks a reasoning model inlines into its reply.</summary>
    private static string StripThinkBlocks(string text) =>
        System.Text.RegularExpressions.Regex.Replace(
            text,
            "<think(?:ing)?>.*?</think(?:ing)?>",
            "",
            System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Yield each top-level balanced {...} substring, honoring quoted strings and escapes.</summary>
    private static IEnumerable<string> JsonObjectSpans(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '{')
            {
                continue;
            }

            var depth = 0;
            var inString = false;
            var escaped = false;
            for (var j = i; j < text.Length; j++)
            {
                var c = text[j];
                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (c == '\\')
                    {
                        escaped = true;
                    }
                    else if (c == '"')
                    {
                        inString = false;
                    }
                }
                else if (c == '"')
                {
                    inString = true;
                }
                else if (c == '{')
                {
                    depth++;
                }
                else if (c == '}' && --depth == 0)
                {
                    yield return text[i..(j + 1)];
                    i = j; // resume scanning after this object
                    break;
                }
            }
        }
    }

    private sealed record SuggestionDto(string? Classification, string? Explanation, string? Confidence);
}
