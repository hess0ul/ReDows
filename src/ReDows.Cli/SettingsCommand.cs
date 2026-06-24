using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ReDows.Core.Settings;
using ReDows.Providers.Windows.Settings;

namespace ReDows.Cli;

/// <summary>
/// 'redows settings' — reads the catalogued Windows settings from the registry
/// (read-only) and writes settings.json (source of truth) + settings.md. The
/// catalog (the list of settings to read) is the YAML under 'settings/'.
/// Exit codes: 0 ok, 1 invalid catalog (fail-closed), 2 usage, 4 unexpected error.
/// </summary>
public static class SettingsCommand
{
    private const string DefaultCatalogDirectory = "settings";

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
        string? outputDirectory = null;
        var catalogDirectory = DefaultCatalogDirectory;
        var asJson = false;
        var byModule = false;

        for (var i = 0; i < options.Length; i++)
        {
            switch (options[i])
            {
                case "--out" when i + 1 < options.Length:
                    outputDirectory = options[++i];
                    break;
                case "--catalog" when i + 1 < options.Length:
                    catalogDirectory = options[++i];
                    break;
                case "--json":
                    asJson = true;
                    break;
                case "--by-module":
                    byModule = true;
                    break;
                default:
                    Console.Error.WriteLine($"Invalid option '{options[i]}'. Usage: redows settings [--out <dir>] [--catalog <dir>] [--json] [--by-module]");
                    return 2;
            }
        }

        SettingsCatalog catalog;
        try
        {
            catalog = SettingsCatalogLoader.LoadDirectory(ResolveCatalog(catalogDirectory));
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

        var report = WindowsSettingsReader.Read(catalog);

        var profile = byModule ? SettingsProfileBuilder.Build(report) : null;
        var markdown = profile is not null ? RenderProfile(profile) : RenderMarkdown(report);
        var json = asJson || outputDirectory is not null
            ? JsonSerializer.Serialize((object?)profile ?? report, JsonOptions)
            : null;
        Console.WriteLine(asJson ? json : markdown);

        if (outputDirectory is not null)
        {
            Directory.CreateDirectory(outputDirectory);
            var stem = byModule ? "settings-profile" : "settings";
            var jsonPath = Path.Combine(outputDirectory, $"{stem}.json");
            var markdownPath = Path.Combine(outputDirectory, $"{stem}.md");
            File.WriteAllText(jsonPath, json!);
            File.WriteAllText(markdownPath, markdown);
            Console.Error.WriteLine($"Written to '{jsonPath}' and '{markdownPath}'.");
        }

        return 0;
    }

    internal static string RenderProfileJson(SettingsProfile profile) => JsonSerializer.Serialize(profile, JsonOptions);

    internal static string RenderProfile(SettingsProfile profile)
    {
        var text = new StringBuilder();
        text.AppendLine("# InDows settings profile (ReDows)");
        text.AppendLine();
        text.AppendLine("The settings ReDows read on this PC, grouped by the InDows module that re-applies them — the \"settings\" half of the ReDows → InDows profile.");

        text.AppendLine();
        text.AppendLine("Comes back automatically (an existing module re-applies it):");

        foreach (var module in profile.ExistingModules)
        {
            text.AppendLine();
            text.AppendLine($"## Module: {module.Module} ({module.Settings.Count})");
            text.AppendLine();
            text.AppendLine("| Setting | Value to apply |");
            text.AppendLine("| --- | --- |");
            foreach (var reading in module.Settings)
            {
                text.AppendLine($"| {Cell(reading.Definition.Name)} | {Cell(Desired(reading))} |");
            }
        }

        if (profile.ByBase.Count > 0)
        {
            text.AppendLine();
            text.AppendLine($"## Comes back via the InDows base install — not a module ({profile.ByBase.Count})");
            text.AppendLine();
            foreach (var reading in profile.ByBase)
            {
                text.AppendLine($"- {Cell(reading.Definition.Name)}: {Cell(Desired(reading))}");
            }
        }

        if (profile.NewModules.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("## New InDows modules to build");
            text.AppendLine();
            foreach (var module in profile.NewModules)
            {
                text.AppendLine($"- **{Cell(module.Module)}** — {Cell(string.Join(", ", module.Settings.Select(s => s.Definition.Name)))}");
            }
        }

        if (profile.PersoOnly.Count > 0)
        {
            text.AppendLine();
            text.AppendLine($"## Private config only — the public InDows leaves the default ({profile.PersoOnly.Count})");
            text.AppendLine();
            foreach (var reading in profile.PersoOnly)
            {
                text.AppendLine($"- {Cell(reading.Definition.Name)}: {Cell(Desired(reading))}");
            }
        }

        if (profile.NotApplied.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("## Module is the home, but the line is OFF today (needs wiring to restore)");
            foreach (var module in profile.NotApplied)
            {
                text.AppendLine();
                text.AppendLine($"### {module.Module} — not applied yet ({module.Settings.Count})");
                text.AppendLine();
                foreach (var reading in module.Settings)
                {
                    text.AppendLine($"- {Cell(reading.Definition.Name)}: {Cell(Desired(reading))}");
                }
            }
        }

        if (profile.Manual.Count > 0)
        {
            text.AppendLine();
            text.AppendLine($"## Nothing restores this — redo by hand after reset ({profile.Manual.Count})");
            text.AppendLine();
            foreach (var reading in profile.Manual)
            {
                text.AppendLine($"- {Cell(reading.Definition.Name)}: {Cell(Desired(reading))}");
            }
        }

        if (profile.NotInLoop.Count > 0)
        {
            text.AppendLine();
            text.AppendLine($"## Read-only — no InDows module, capture only ({profile.NotInLoop.Count})");
            text.AppendLine();
            foreach (var reading in profile.NotInLoop.OrderBy(r => r.Definition.Name, StringComparer.OrdinalIgnoreCase))
            {
                text.AppendLine($"- {Cell(reading.Definition.Name)}: {Cell(Desired(reading))}");
            }
        }

        if (profile.Unreadable.Count > 0)
        {
            text.AppendLine();
            text.AppendLine($"## Unreadable ({profile.Unreadable.Count})");
            text.AppendLine();
            foreach (var reading in profile.Unreadable.OrderBy(r => r.Definition.Name, StringComparer.OrdinalIgnoreCase))
            {
                text.AppendLine($"- {Cell(reading.Definition.Name)}: {Cell(reading.Error)}");
            }
        }

        return text.ToString();
    }

    /// <summary>The value InDows should re-apply: the current value, or the default when absent.</summary>
    private static string Desired(SettingReading reading) =>
        reading.Present ? $"{reading.RawValue} ({reading.Meaning})" : reading.Meaning;

    internal static string ResolveCatalog(string requested)
    {
        if (requested != DefaultCatalogDirectory || Directory.Exists(requested))
        {
            return requested;
        }

        var nextToExe = Path.Combine(AppContext.BaseDirectory, DefaultCatalogDirectory);
        return Directory.Exists(nextToExe) ? nextToExe : requested;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static string RenderJson(SettingsReport report) => JsonSerializer.Serialize(report, JsonOptions);

    private static string RenderMarkdown(SettingsReport report)
    {
        var text = new StringBuilder();
        text.AppendLine("# Windows settings report (ReDows)");
        text.AppendLine();
        text.AppendLine($"{report.Readings.Count} settings read. `present` = a registry value exists; otherwise the setting sits at its Windows default.");

        foreach (var category in report.Readings
            .GroupBy(r => r.Definition.Category, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            text.AppendLine();
            text.AppendLine($"## {category.Key}");
            text.AppendLine();
            text.AppendLine("| Setting | State | Meaning | In loop | InDows module |");
            text.AppendLine("| --- | --- | --- | --- | --- |");
            foreach (var reading in category.OrderBy(r => r.Definition.Name, StringComparer.OrdinalIgnoreCase))
            {
                var state = reading.Error is not null ? "unreadable"
                    : reading.Present ? $"set (`{reading.RawValue}`){(reading.IsDefault ? " = default" : "")}"
                    : "default";
                text.AppendLine(
                    $"| {Cell(reading.Definition.Name)} | {Cell(state)} | {Cell(reading.Meaning)} | {(reading.Definition.InLoop ? "yes" : "no")} | {Cell(reading.Definition.IndowsModule)} |");
            }
        }

        if (report.Notes.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("## Notes");
            text.AppendLine();
            foreach (var note in report.Notes)
            {
                text.AppendLine($"- {Cell(note)}");
            }
        }

        text.AppendLine();
        text.AppendLine("## Declared limits (V1)");
        text.AppendLine();
        foreach (var limit in report.DeclaredLimits)
        {
            text.AppendLine($"- {limit}");
        }

        return text.ToString();
    }

    /// <summary>Markdown-table-safe cell: no pipes, no control/bidi characters, single line.</summary>
    private static string Cell(string? value) =>
        string.IsNullOrEmpty(value) ? "" : ConsoleText.Sanitize(value).Replace('|', '/');
}
