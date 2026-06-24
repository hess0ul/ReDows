using System.Text.Json;
using System.Text.Json.Serialization;
using ReDows.Core.Apps;

namespace ReDows.Cli;

/// <summary>
/// 'redows export' — turn the apps inventory into an InDows-ready winget catalog
/// (configuration.dsc.yaml). Reads an existing apps.json (produced by
/// 'redows apps --enrich-winget --out &lt;dir&gt;'): read-only, no machine side effects.
/// Exit codes: 0 ok, 2 usage, 3 input missing/invalid, 4 unexpected error.
/// </summary>
public static class ExportCommand
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
        string? outPath = null;

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
                case "--out" when i + 1 < options.Length:
                    outPath = options[++i];
                    break;
                default:
                    Console.Error.WriteLine(
                        $"Invalid option '{options[i]}'. Usage: redows export [--target indows] [--from <apps.json>] [--out <file>]");
                    return 2;
            }
        }

        if (!string.Equals(target, "indows", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"Unknown export target '{target}'. Supported: indows.");
            return 2;
        }

        var (catalog, error) = TryBuildCatalog(fromPath);
        if (catalog is null)
        {
            Console.Error.WriteLine(error);
            return 3;
        }

        if (outPath is null)
        {
            Console.Out.Write(catalog.Yaml);
        }
        else
        {
            File.WriteAllText(outPath, catalog.Yaml);
            Console.Error.WriteLine($"InDows catalog written to '{outPath}'.");
        }

        Console.Error.WriteLine(
            $"{catalog.ActiveCount} app(s) ready via winget; {catalog.CandidateCount} uncertain + {catalog.ManualCount} without an id left as comments to review.");
        return 0;
    }

    /// <summary>
    /// Read an apps.json and emit the InDows winget catalog, read-only. Returns the catalog,
    /// or null plus a user-facing message (input missing or not valid apps.json → caller exits 3).
    /// Shared by 'redows export' and 'redows profile'.
    /// </summary>
    internal static (InDowsCatalog? Catalog, string? Error) TryBuildCatalog(string fromPath)
    {
        if (!File.Exists(fromPath))
        {
            return (null,
                $"Input not found: '{fromPath}'. Run 'redows apps --enrich-winget --out <dir>' first, then point --from at its apps.json.");
        }

        try
        {
            var artifact = JsonSerializer.Deserialize<AppsArtifact>(File.ReadAllText(fromPath), JsonOptions);
            return (InDowsCatalogEmitter.Emit(artifact?.Entries ?? []), null);
        }
        catch (JsonException ex)
        {
            return (null, $"Cannot read inventory '{fromPath}': not a valid apps.json ({ex.Message}).");
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Minimal view of apps.json: only the entries are needed to emit a catalog.</summary>
    private sealed record AppsArtifact(List<AppEntry> Entries);
}
