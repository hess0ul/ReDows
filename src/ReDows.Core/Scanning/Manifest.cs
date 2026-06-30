using System.Text.Json;
using ReDows.Core.Rules;

namespace ReDows.Core.Scanning;

// Alias so the type name beats the ReDows.Core.Classification namespace lookup.
using Classification = ReDows.Core.Classification.Classification;

/// <summary>
/// One line of the capture manifest: a single CAPTURE item named in full — its path
/// (the engine's normalized, '/'-separated form), the verdict, the rule that decided
/// it, the rule's stage, its logical size, whether it is a directory, and any flags.
/// <para>
/// The manifest records a captured object's <em>location</em>, never its content — a
/// secret file's path is listed (so the copy step knows it exists and where), but its
/// value is never read here (invariant: secrets kept apart, never in clear).
/// </para>
/// </summary>
public sealed record ManifestEntry(
    string Path,
    string Verdict,
    string Rule,
    string Stage,
    long Bytes,
    bool IsDirectory,
    IReadOnlyList<string> Flags)
{
    /// <summary>Build a manifest entry from a classified scan item, using the canonical string forms.</summary>
    public static ManifestEntry From(string path, Classification classification, long bytes, bool isDirectory) =>
        new(
            path,
            classification.Verdict.Format(),
            classification.RuleId,
            classification.Stage,
            bytes,
            isDirectory,
            classification.Flags is null ? [] : classification.Flags.Select(f => f.Format()).ToList());
}

/// <summary>Pure JSON-Lines serializer: one compact JSON object per manifest entry, no trailing newline.</summary>
public static class ManifestLine
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    public static string Format(ManifestEntry entry) => JsonSerializer.Serialize(entry, Options);

    /// <summary>Parse one JSONL manifest line back into an entry; null on a blank or malformed line.</summary>
    public static ManifestEntry? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ManifestEntry>(line, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
