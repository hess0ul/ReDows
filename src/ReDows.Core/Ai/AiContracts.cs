namespace ReDows.Core.Ai;

/// <summary>One child entry of the folder being analyzed: its NAME and size only — never its content.</summary>
public sealed record FolderFact(string Name, bool IsDirectory, long Bytes);

/// <summary>
/// EVERYTHING the AI is allowed to see about a folder — a strict whitelist, and the privacy invariant
/// of the AI assistant: folder path and name, total size, whether an executable is present, and the
/// NAMES/sizes of its children. No field can carry file contents, and <see cref="AiPayload.Build"/>
/// (the only producer) works from an already-listed directory — it never opens a file.
/// </summary>
public sealed record FolderMetadata(
    string FolderPath,
    string FolderName,
    long TotalBytes,
    bool HasExecutable,
    IReadOnlyList<FolderFact> Children,
    int ChildrenOmitted);

/// <summary>
/// What the AI proposes for a folder — a SUGGESTION, never a decision (the user accepts or refuses).
/// <see cref="Classification"/> is one of: keep (user data), drop (re-downloadable bundle / cache),
/// mixed (both — look inside), unknown (the model can't tell). <see cref="Confidence"/>: high/medium/low.
/// </summary>
public sealed record AiSuggestion(string Classification, string Explanation, string Confidence)
{
    public const string Keep = "keep";
    public const string Drop = "drop";
    public const string Mixed = "mixed";
    public const string Unknown = "unknown";
}

/// <summary>
/// Analyzes a folder's metadata with an AI model. A seam: the real implementation talks to an
/// OpenAI-compatible endpoint (a local LM Studio / Ollama by default); a test swaps a fake.
/// The ONLY input is the whitelisted <see cref="FolderMetadata"/> — nothing else ever leaves the PC.
/// </summary>
public interface IAiProvider
{
    Task<AiSuggestion> AnalyzeAsync(FolderMetadata folder, CancellationToken cancellationToken);
}
