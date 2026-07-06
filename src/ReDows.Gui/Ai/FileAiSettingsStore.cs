using System.IO;
using System.Text.Json;

namespace ReDows.Gui.Ai;

/// <summary>
/// The real AI-settings store: ai-settings.json under %LocalAppData%\ReDows, next to the session file.
/// Best-effort like <c>FileSessionStore</c>: missing/unreadable = defaults, a failed save is swallowed.
/// The path is injectable so it can be unit-tested against a temp file.
/// </summary>
public sealed class FileAiSettingsStore : IAiSettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _path;

    public FileAiSettingsStore()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReDows", "ai-settings.json"))
    {
    }

    public FileAiSettingsStore(string path) => _path = path;

    public AiSettings? Load()
    {
        try
        {
            return File.Exists(_path) ? JsonSerializer.Deserialize<AiSettings>(File.ReadAllText(_path), Options) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Save(AiSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(settings, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Persistence is a convenience; never let a failed save break the app.
        }
    }
}
