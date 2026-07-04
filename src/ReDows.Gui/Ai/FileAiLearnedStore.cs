using System.IO;
using System.Text.Json;

namespace ReDows.Gui.Ai;

/// <summary>
/// The real learned-drops store: ai-learned.json under %LocalAppData%\ReDows, next to the other
/// ReDows files. Holds folder PATHS and sizes only (the user's accepted "safe to drop" lessons) —
/// never file contents, never anything secret. Best-effort; path injectable for tests.
/// </summary>
public sealed class FileAiLearnedStore : IAiLearnedStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _path;

    public FileAiLearnedStore()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReDows", "ai-learned.json"))
    {
    }

    public FileAiLearnedStore(string path) => _path = path;

    public IReadOnlyList<LearnedDrop> Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<List<LearnedDrop>>(File.ReadAllText(_path), Options) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    public void Save(IReadOnlyList<LearnedDrop> drops)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(drops, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Remembering is a convenience — never let a failed save break the app.
        }
    }
}
