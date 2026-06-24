using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ReDows.Core.Secrets;
using ReDows.Providers.Windows.Secrets;

namespace ReDows.Cli;

/// <summary>
/// 'redows secrets' — inspects the registry locations where some apps keep secrets/config
/// that a file scan misses (PuTTY, WinSCP, Remote Desktop, MPC-HC, TightVNC). READ-ONLY,
/// and NAMES only: a value classified as a secret is recorded by its location, never read.
/// The headline output is the "export BEFORE the reset" alert list. Exit codes: 0 ok,
/// 1 invalid catalog (fail-closed), 2 usage, 4 unexpected error.
/// </summary>
public static class SecretsCommand
{
    private const string DefaultCatalogDirectory = "secrets";

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
                default:
                    Console.Error.WriteLine($"Invalid option '{options[i]}'. Usage: redows secrets [--out <dir>] [--catalog <dir>] [--json]");
                    return 2;
            }
        }

        RegistrySecretsCatalog catalog;
        try
        {
            catalog = RegistrySecretsCatalogLoader.LoadDirectory(ResolveCatalog(catalogDirectory));
        }
        catch (RegistrySecretsCatalogException ex)
        {
            Console.Error.WriteLine($"Registry-secrets catalog INVALID — {ex.Errors.Count} error(s). Refusing to read (fail-closed).");
            foreach (var error in ex.Errors)
            {
                Console.Error.WriteLine($"  - {error}");
            }

            return 1;
        }

        var reader = new RegistrySecretsReader();
        var report = RegistrySecretsScan.Run(catalog, reader.Read);

        var markdown = RenderText(report);
        var json = asJson || outputDirectory is not null ? JsonSerializer.Serialize(report, JsonOptions) : null;
        Console.WriteLine(asJson ? json : markdown);

        if (outputDirectory is not null)
        {
            Directory.CreateDirectory(outputDirectory);
            File.WriteAllText(Path.Combine(outputDirectory, "secrets.json"), json!);
            File.WriteAllText(Path.Combine(outputDirectory, "secrets.md"), markdown);
            Console.Error.WriteLine($"Written to '{Path.Combine(outputDirectory, "secrets.json")}' and '{Path.Combine(outputDirectory, "secrets.md")}'.");
        }

        return 0;
    }

    private static string RenderText(RegistrySecretsReport report)
    {
        var text = new StringBuilder();
        text.AppendLine("== ReDows registry secrets ==");
        text.AppendLine("Read-only inventory of secrets/config kept ONLY in the registry (a file scan misses these). Locations, never values.");
        text.AppendLine();
        text.AppendLine("== Accounting (every target counted once) ==");
        text.AppendLine($"  present, has a secret : {report.PresentWithSecret}");
        text.AppendLine($"  present, no secret    : {report.PresentNoSecret}");
        text.AppendLine($"  absent (not used)     : {report.Absent}");
        text.AppendLine($"  unreadable            : {report.Unreadable}");
        text.AppendLine($"  equation: {report.PresentWithSecret} + {report.PresentNoSecret} + {report.Absent} + {report.Unreadable} = {report.Total} targets");

        if (report.PreResetAlerts.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("== EXPORT BEFORE THE RESET — these secrets live only here ==");
            foreach (var finding in report.PreResetAlerts)
            {
                var secrets = finding.Items.Count(i => i.Class == SecretClass.Secret);
                text.AppendLine($"  • {Cell(finding.Target.App)} — {Cell(finding.Target.What)}");
                text.AppendLine($"      {secrets} secret value(s); {Sensitivity(finding.Target.Sensitivity)}");
                if (finding.Target.ExportHint is { } hint)
                {
                    text.AppendLine($"      → {Cell(hint)}");
                }
            }
        }

        text.AppendLine();
        text.AppendLine("== Per target (config values shown; secret values never read) ==");
        foreach (var finding in report.Findings)
        {
            var detail = finding.Status switch
            {
                TargetStatus.Unreadable => $"unreadable ({Cell(finding.Error)})",
                TargetStatus.Absent => "absent",
                _ => $"{finding.Items.Count(i => i.Class == SecretClass.Secret)} secret / "
                    + $"{finding.Items.Count(i => i.Class == SecretClass.Config)} config / "
                    + $"{finding.Items.Count(i => i.Class == SecretClass.Review)} review",
            };
            text.AppendLine($"  {Cell(finding.Target.Id),-22} {Status(finding.Status),-20} {detail}");

            foreach (var item in finding.Items.Where(i => i.Class == SecretClass.Config))
            {
                var shown = item.Value is { } v ? $"{item.ValueName} = {Truncate(v)}" : item.ValueName;
                text.AppendLine($"      {Cell(shown)}");
            }

            foreach (var item in finding.Items.Where(i => i.Class == SecretClass.Secret))
            {
                text.AppendLine($"      {Cell(item.ValueName)} = (secret — present, not read)");
            }
        }

        var review = report.Findings.SelectMany(f => f.Items).Where(i => i.Class == SecretClass.Review).ToList();
        if (review.Count > 0)
        {
            text.AppendLine();
            text.AppendLine($"== Unknown names in a secrets zone — REVIEW (presence only) ({review.Count}) ==");
            foreach (var item in review)
            {
                text.AppendLine($"  {Cell(item.Location)}");
            }
        }

        return text.ToString();
    }

    private static string Status(TargetStatus status) => status switch
    {
        TargetStatus.PresentWithSecret => "has-secret",
        TargetStatus.PresentNoSecret => "present",
        TargetStatus.Absent => "absent",
        TargetStatus.Unreadable => "unreadable",
        _ => status.ToString(),
    };

    private static string Sensitivity(SecretSensitivity sensitivity) => sensitivity switch
    {
        SecretSensitivity.ReversibleSecret => "recoverable from the registry — export it, then wipe the disk",
        SecretSensitivity.StrongSecret => "protected (DPAPI/master password) — UNREADABLE after a reset, export it first",
        _ => "config only",
    };

    private static string ResolveCatalog(string requested)
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

    private static string Truncate(string value) => value.Length > 120 ? value[..117] + "…" : value;

    private static string Cell(string? value) =>
        string.IsNullOrEmpty(value) ? "" : ConsoleText.Sanitize(value);
}
