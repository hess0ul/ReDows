using ReDows.Core.Profile;
using ReDows.Core.Settings;
using ReDows.Providers.Windows.Settings;

namespace ReDows.Cli;

/// <summary>
/// 'redows profile' — write the complete InDows profile folder that closes the
/// ReDows → InDows loop: the apps catalog (configuration.dsc.yaml), the settings
/// grouped by module (settings-profile.json/.md) and a plain-language README that
/// lists what InDows restores automatically and what must be redone by hand.
/// Reads an existing apps.json and reads the live registry/settings — read-only,
/// no machine side effects. Exit: 0 ok, 1 invalid catalog, 2 usage, 3 input, 4 error.
/// </summary>
public static class ProfileCommand
{
    public static int Run(string[] options)
    {
        try
        {
            return RunCore(options);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Unexpected error: {ex.GetType().Name}: {ex.Message}");
            return 4;
        }
    }

    private static int RunCore(string[] options)
    {
        var target = "indows";
        var fromPath = "apps.json";
        var catalogDirectory = "settings";
        string? outDirectory = null;

        for (var i = 0; i < options.Length; i++)
        {
            switch (options[i])
            {
                case "--target" when i + 1 < options.Length:
                    target = options[++i];
                    break;
                case "--from" when i + 1 < options.Length:
                    fromPath = options[++i];
                    break;
                case "--catalog" when i + 1 < options.Length:
                    catalogDirectory = options[++i];
                    break;
                case "--out" when i + 1 < options.Length:
                    outDirectory = options[++i];
                    break;
                default:
                    Console.Error.WriteLine(
                        $"Invalid option '{options[i]}'. Usage: redows profile --out <dir> [--target indows] [--from <apps.json>] [--catalog <dir>]");
                    return 2;
            }
        }

        if (!string.Equals(target, "indows", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"Unknown profile target '{target}'. Supported: indows.");
            return 2;
        }

        if (outDirectory is null)
        {
            Console.Error.WriteLine("Missing --out <dir>. The profile is a folder of files. Usage: redows profile --out <dir> [--from <apps.json>] [--catalog <dir>]");
            return 2;
        }

        // Apps half — read apps.json, emit the winget catalog (read-only).
        var (catalog, appsError) = ExportCommand.TryBuildCatalog(fromPath);
        if (catalog is null)
        {
            Console.Error.WriteLine(appsError);
            return 3;
        }

        // Settings half — load the catalog (fail-closed), read the live machine (read-only).
        SettingsCatalog settingsCatalog;
        try
        {
            settingsCatalog = SettingsCatalogLoader.LoadDirectory(SettingsCommand.ResolveCatalog(catalogDirectory));
        }
        catch (SettingsCatalogException ex)
        {
            Console.Error.WriteLine($"Settings catalog INVALID — {ex.Errors.Count} error(s). Refusing to read (fail-closed).");
            foreach (var error in ex.Errors)
            {
                Console.Error.WriteLine($"  - {error}");
            }

            return 1;
        }

        var report = WindowsSettingsReader.Read(settingsCatalog);
        var profile = SettingsProfileBuilder.Build(report);

        Directory.CreateDirectory(outDirectory);
        File.WriteAllText(Path.Combine(outDirectory, "configuration.dsc.yaml"), catalog.Yaml);
        File.WriteAllText(Path.Combine(outDirectory, "settings-profile.json"), SettingsProfileEmitter.RenderJson(profile));
        File.WriteAllText(Path.Combine(outDirectory, "settings-profile.md"), SettingsProfileEmitter.RenderMarkdown(profile));
        File.WriteAllText(Path.Combine(outDirectory, "README.md"), InDowsProfileReadme.Render(catalog, profile));

        Console.Error.WriteLine($"InDows profile written to '{outDirectory}' (configuration.dsc.yaml, settings-profile.json, settings-profile.md, README.md).");
        Console.WriteLine(
            $"{catalog.ActiveCount} app(s) via winget; settings across {profile.ExistingModules.Count} module(s); " +
            $"{profile.Manual.Count + profile.NotApplied.Sum(m => m.Settings.Count) + profile.PersoOnly.Count} setting(s) need a manual touch — see README.md.");
        return 0;
    }
}
