using System.IO;
using System.Text.Json;

namespace ReDows.Gui.Scanning;

/// <summary>
/// Remembers the user's per-category keep / review / ignore choices between launches, so the Scan screen
/// starts with what they last picked instead of every category back on "review". A seam: the real
/// implementation is a small JSON file; a test swaps a fake. Best-effort — a missing or broken file just
/// means the defaults, and a failed save never breaks the scan.
/// </summary>
public interface IModuleSettingsStore
{
    /// <summary>Saved actions by module name ("keep" / "review" / "ignore"); empty when nothing was saved.</summary>
    IReadOnlyDictionary<string, string> Load();

    void Save(IReadOnlyDictionary<string, string> actionsByModule);
}

/// <summary>
/// The real store: module-settings.json under %LocalAppData%\ReDows, next to the session and AI files.
/// Best-effort like <see cref="ReDows.Gui.Ai.FileAiSettingsStore"/>. The path is injectable so it can be
/// unit-tested against a temp file. Only the plain keep/review/ignore choice is stored — nothing secret.
/// </summary>
public sealed class WindowsModuleSettingsStore : IModuleSettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _path;

    public WindowsModuleSettingsStore()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReDows", "module-settings.json"))
    {
    }

    public WindowsModuleSettingsStore(string path) => _path = path;

    public IReadOnlyDictionary<string, string> Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path), Options) ?? new Dictionary<string, string>()
                : new Dictionary<string, string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    public void Save(IReadOnlyDictionary<string, string> actionsByModule)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(actionsByModule, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Persistence is a convenience — never let a failed save break the app.
        }
    }
}
