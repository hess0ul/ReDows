using System.IO;
using System.Text.Json;

namespace ReDows.Gui.Session;

/// <summary>
/// The real session store: a JSON file (session.json) under %LocalAppData%\ReDows, next to the scan
/// manifest. Best-effort — a missing or unreadable file just means "no session yet" (Load returns null),
/// and a save that fails is swallowed (persistence is a convenience, never allowed to break the app).
/// The path is injectable so it can be unit-tested against a temp file.
/// </summary>
public sealed class FileSessionStore : ISessionStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _path;

    public FileSessionStore()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReDows", "session.json"))
    {
    }

    public FileSessionStore(string path) => _path = path;

    public SessionFile? Load()
    {
        try
        {
            return File.Exists(_path) ? JsonSerializer.Deserialize<SessionFile>(File.ReadAllText(_path), Options) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Save(SessionFile session)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(session, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Persistence is a convenience — never let a failed save break the app.
        }
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
