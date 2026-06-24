using Microsoft.Win32;
using ReDows.Core.Apps;

namespace ReDows.Providers.Windows.Apps;

/// <summary>
/// Steam: root from the registry, then EVERY library in libraryfolders.vdf
/// (games often live on other volumes). A library on an absent volume is a
/// counted degradation with an alert — never a silent gap.
/// </summary>
public static class SteamAppSource
{
    public sealed record Result(
        IReadOnlyList<AppEntry> Entries,
        SourceAccounting Accounting,
        IReadOnlyList<InventoryDegradation> Degradations);

    public static Result Enumerate()
    {
        var entries = new List<AppEntry>();
        var degradations = new List<InventoryDegradation>();
        long enumerated = 0, apps = 0, errors = 0;

        var steamRoot = FindSteamRoot();
        if (steamRoot is not null)
        {
            foreach (var library in FindLibraries(steamRoot, degradations))
            {
                var steamApps = Path.Combine(library, "steamapps");
                IEnumerable<string> manifests;
                try
                {
                    manifests = Directory.EnumerateFiles(steamApps, "appmanifest_*.acf");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    degradations.Add(new InventoryDegradation("steam", steamApps,
                        $"library not enumerable ({ex.GetType().Name}) — its games are not inventoried"));
                    continue;
                }

                foreach (var manifest in manifests)
                {
                    enumerated++;
                    try
                    {
                        var app = SteamManifests.ParseAppManifest(File.ReadAllText(manifest));
                        if (app is null)
                        {
                            errors++;
                            continue;
                        }

                        var installLocation = app.InstallDir is { Length: > 0 }
                            ? Path.Combine(steamApps, "common", app.InstallDir)
                            : null;
                        var stateNote = app.StateFlags is { } flags && flags != 4
                            ? $"not fully installed (StateFlags={flags})"
                            : null;

                        apps++;
                        entries.Add(new AppEntry(
                            Key: $"steam:{app.AppId}",
                            Source: "steam",
                            Kind: AppEntryKind.App,
                            Name: app.Name ?? $"steam app {app.AppId}",
                            Version: null,
                            Publisher: null,
                            InstallLocation: installLocation,
                            Scope: "machine",
                            Reinstall: new ReinstallHint("steam", app.AppId, ReinstallConfidence.Exact),
                            Note: stateNote ?? InstallLocationCheck.Note(installLocation)));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        errors++;
                    }
                }
            }
        }

        return new Result(entries, new SourceAccounting("steam", enumerated, apps, 0, 0, errors), degradations);
    }

    private static string? FindSteamRoot()
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
        using var machineKey = hklm.OpenSubKey(@"SOFTWARE\Valve\Steam");
        if (machineKey?.GetValue("InstallPath") is string machinePath && Directory.Exists(machinePath))
        {
            return machinePath;
        }

        using var userKey = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Valve\Steam");
        var userPath = (userKey?.GetValue("SteamPath") as string)?.Replace('/', '\\');
        return userPath is not null && Directory.Exists(userPath) ? userPath : null;
    }

    private static IReadOnlyList<string> FindLibraries(string steamRoot, List<InventoryDegradation> degradations)
    {
        var libraries = new List<string> { steamRoot };
        var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        try
        {
            if (File.Exists(vdf))
            {
                foreach (var path in SteamManifests.GetLibraryPaths(File.ReadAllText(vdf)))
                {
                    if (Directory.Exists(path))
                    {
                        libraries.Add(path);
                    }
                    else
                    {
                        var root = Path.GetPathRoot(path);
                        degradations.Add(new InventoryDegradation("steam", path,
                            string.IsNullOrEmpty(root) || Directory.Exists(root)
                                ? "library path missing — stale libraryfolders entry?"
                                : InstallLocationCheck.VolumeAbsentAlert));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            degradations.Add(new InventoryDegradation("steam", vdf,
                $"libraryfolders.vdf unreadable ({ex.GetType().Name}) — extra libraries unknown"));
        }

        return libraries.DistinctBy(p => p.TrimEnd('\\', '/'), StringComparer.OrdinalIgnoreCase).ToList();
    }
}

/// <summary>Epic Games Launcher: ProgramData manifests (*.item JSON).</summary>
public static class EpicAppSource
{
    public sealed record Result(IReadOnlyList<AppEntry> Entries, SourceAccounting Accounting);

    public static Result Enumerate()
    {
        var entries = new List<AppEntry>();
        long enumerated = 0, apps = 0, errors = 0;

        var manifestsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (Directory.Exists(manifestsDirectory))
        {
            foreach (var item in Directory.EnumerateFiles(manifestsDirectory, "*.item"))
            {
                enumerated++;
                try
                {
                    var app = EpicManifests.ParseItem(File.ReadAllText(item));
                    if (app is null)
                    {
                        errors++;
                        continue;
                    }

                    apps++;
                    entries.Add(new AppEntry(
                        Key: $"epic:{app.AppName}",
                        Source: "epic",
                        Kind: AppEntryKind.App,
                        Name: app.DisplayName ?? app.AppName,
                        Version: app.Version,
                        Publisher: null,
                        InstallLocation: app.InstallLocation,
                        Scope: "machine",
                        Reinstall: new ReinstallHint("epic", app.AppName, ReinstallConfidence.Exact),
                        Note: InstallLocationCheck.Note(app.InstallLocation)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                {
                    errors++;
                }
            }
        }

        return new Result(entries, new SourceAccounting("epic", enumerated, apps, 0, 0, errors));
    }
}

/// <summary>Ubisoft Connect: registry Installs + the matching ARP display name.</summary>
public static class UbisoftAppSource
{
    public sealed record Result(IReadOnlyList<AppEntry> Entries, SourceAccounting Accounting);

    public static Result Enumerate()
    {
        var entries = new List<AppEntry>();
        long enumerated = 0, apps = 0, errors = 0;

        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
        using var installs = hklm.OpenSubKey(@"SOFTWARE\Ubisoft\Launcher\Installs");
        if (installs is not null)
        {
            foreach (var gameId in installs.GetSubKeyNames())
            {
                enumerated++;
                try
                {
                    using var key = installs.OpenSubKey(gameId);
                    var installDir = (key?.GetValue("InstallDir") as string)?.Replace('/', '\\');

                    using var arp = hklm.OpenSubKey(
                        @$"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Uplay Install {gameId}");
                    var displayName = arp?.GetValue("DisplayName") as string;

                    apps++;
                    entries.Add(new AppEntry(
                        Key: $"ubisoft:{gameId}",
                        Source: "ubisoft",
                        Kind: AppEntryKind.App,
                        Name: displayName ?? $"Ubisoft game {gameId}",
                        Version: null,
                        Publisher: "Ubisoft",
                        InstallLocation: installDir,
                        Scope: "machine",
                        Reinstall: new ReinstallHint("ubisoft", gameId, ReinstallConfidence.Exact),
                        Note: InstallLocationCheck.Note(installDir)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    errors++;
                }
            }
        }

        return new Result(entries, new SourceAccounting("ubisoft", enumerated, apps, 0, 0, errors));
    }
}

/// <summary>GOG: per-game registry keys (the Galaxy SQLite database is a later step).</summary>
public static class GogAppSource
{
    public sealed record Result(IReadOnlyList<AppEntry> Entries, SourceAccounting Accounting);

    public static Result Enumerate()
    {
        var entries = new List<AppEntry>();
        long enumerated = 0, apps = 0, errors = 0;

        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
        using var games = hklm.OpenSubKey(@"SOFTWARE\GOG.com\Games");
        if (games is not null)
        {
            foreach (var gameId in games.GetSubKeyNames())
            {
                enumerated++;
                try
                {
                    using var key = games.OpenSubKey(gameId);

                    // Read everything BEFORE counting: a throwing registry read
                    // after apps++ would also hit errors++ and break the
                    // per-source accounting equation (enumerated = apps + errors).
                    var path = key?.GetValue("path") as string;
                    var name = key?.GetValue("gameName") as string ?? $"GOG game {gameId}";
                    var version = key?.GetValue("ver") as string;
                    var note = InstallLocationCheck.Note(path);

                    apps++;
                    entries.Add(new AppEntry(
                        Key: $"gog:{gameId}",
                        Source: "gog",
                        Kind: AppEntryKind.App,
                        Name: name,
                        Version: version,
                        Publisher: null,
                        InstallLocation: path,
                        Scope: "machine",
                        Reinstall: new ReinstallHint("gog", gameId, ReinstallConfidence.Exact),
                        Note: note));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    errors++;
                }
            }
        }

        return new Result(entries, new SourceAccounting("gog", enumerated, apps, 0, 0, errors));
    }
}
