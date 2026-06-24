using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ReDows.Core.Apps;
using ReDows.Providers.Windows.Apps;

namespace ReDows.Cli;

/// <summary>
/// 'redows apps' — the installed-applications inventory (reinstall list).
/// Writes apps.json (source of truth) and apps.md next to it when --out is
/// given. Exit codes: 0 ok, 2 usage, 4 unexpected error.
/// </summary>
public static class AppsCommand
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
        string? outputDirectory = null;
        var enrichWithWinget = false;
        var asJson = false;

        for (var i = 0; i < options.Length; i++)
        {
            switch (options[i])
            {
                case "--out" when i + 1 < options.Length:
                    outputDirectory = options[++i];
                    break;
                case "--enrich-winget":
                    enrichWithWinget = true;
                    break;
                case "--json":
                    asJson = true;
                    break;
                default:
                    Console.Error.WriteLine($"Invalid option '{options[i]}'. Usage: redows apps [--out <dir>] [--enrich-winget] [--json]");
                    return 2;
            }
        }

        if (enrichWithWinget)
        {
            Console.Error.WriteLine(
                "winget enrichment enabled (opt-in): winget will RUN on this machine — first run may persist source-agreement acceptance and write state in the user profile.");
        }

        var report = AppInventoryProvider.Build(enrichWithWinget);

        var markdown = RenderMarkdown(report);
        var json = asJson || outputDirectory is not null ? RenderJson(report) : null;
        Console.WriteLine(asJson ? json : markdown);

        if (outputDirectory is not null)
        {
            Directory.CreateDirectory(outputDirectory);
            var jsonPath = Path.Combine(outputDirectory, "apps.json");
            var markdownPath = Path.Combine(outputDirectory, "apps.md");
            File.WriteAllText(jsonPath, json!);
            File.WriteAllText(markdownPath, markdown);
            Console.Error.WriteLine($"Inventory written to '{jsonPath}' and '{markdownPath}'.");
        }

        return 0;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static string RenderJson(AppInventoryReport report) =>
        JsonSerializer.Serialize(report, JsonOptions);

    private static string RenderMarkdown(AppInventoryReport report)
    {
        var text = new StringBuilder();
        text.AppendLine("# Installed applications inventory (ReDows)");
        text.AppendLine();

        text.AppendLine("## Accounting by source");
        text.AppendLine();
        text.AppendLine("| Source | Enumerated | Apps | Components | Updates | Errors | Unaccounted |");
        text.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");
        foreach (var source in report.Sources)
        {
            text.AppendLine(
                $"| {source.Source} | {source.Enumerated} | {source.Apps} | {source.Components} | {source.Updates} | {source.Errors} | {source.Unaccounted} |");
        }

        text.AppendLine();
        text.AppendLine(report.TotalUnaccounted == 0
            ? "Every enumerated entry is accounted for (apps + components + updates + errors). ✓"
            : $"⚠️ {report.TotalUnaccounted} entries unaccounted — inventory invalid (engine bug).");

        foreach (var kind in (AppEntryKind[])[AppEntryKind.App, AppEntryKind.Component, AppEntryKind.Update])
        {
            var bucket = report.Entries
                .Where(e => e.Kind == kind)
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (bucket.Count == 0)
            {
                continue;
            }

            text.AppendLine();
            text.AppendLine(kind switch
            {
                AppEntryKind.App => $"## Applications to reinstall ({bucket.Count})",
                AppEntryKind.Component => $"## Components bucket — review, not silently dropped ({bucket.Count})",
                _ => $"## Updates bucket ({bucket.Count})",
            });
            text.AppendLine();
            text.AppendLine("| Name | Version | Publisher | Source | Scope | Reinstall | Note |");
            text.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");
            foreach (var entry in bucket)
            {
                var reinstall = entry.Reinstall is { } hint
                    ? $"{hint.Kind}:{hint.Id}{(hint.Confidence == ReinstallConfidence.Candidate ? " (candidate)" : "")}"
                    : "manual";
                text.AppendLine(
                    $"| {Cell(entry.Name)} | {Cell(entry.Version)} | {Cell(entry.Publisher)} | {entry.Source} | {entry.Scope} | {Cell(reinstall)} | {Cell(entry.Note)} |");
            }
        }

        if (report.Degradations.Count > 0)
        {
            text.AppendLine();
            text.AppendLine($"## Degradations — counted, never guessed ({report.Degradations.Count})");
            text.AppendLine();
            foreach (var degradation in report.Degradations)
            {
                text.AppendLine($"- `{degradation.Source}` / {Cell(degradation.Subject)}: {Cell(degradation.Reason)}");
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
    private static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        return ConsoleText.Sanitize(value).Replace('|', '/');
    }
}
