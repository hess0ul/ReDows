using ReDows.Cli;
using ReDows.Core;
using ReDows.Core.Rules.Loading;

try
{
    // Report glyphs (✓ ✗ → …) degrade to '?' on OEM code pages without this.
    Console.OutputEncoding = System.Text.Encoding.UTF8;
}
catch (Exception ex) when (ex is IOException or System.Security.SecurityException or PlatformNotSupportedException)
{
    // Legacy console host refusing UTF-8: cosmetic only, keep going.
}

return args switch
{
    ["--version"] or ["-v"] => Version(),
    ["rules", "validate", .. var rest] => RulesValidate(rest),
    ["rules", "schema", .. var rest] => RulesSchema(rest),
    ["context", "show"] => ContextCommand.Show(asJson: false),
    ["context", "show", "--json"] => ContextCommand.Show(asJson: true),
    ["scan", .. var scanOptions] => ScanCommand.Run(scanOptions),
    ["apps", .. var appsOptions] => AppsCommand.Run(appsOptions),
    ["settings", .. var settingsOptions] => SettingsCommand.Run(settingsOptions),
    ["secrets", .. var secretsOptions] => SecretsCommand.Run(secretsOptions),
    ["export", .. var exportOptions] => ExportCommand.Run(exportOptions),
    ["profile", .. var profileOptions] => ProfileCommand.Run(profileOptions),
    [] or ["--help"] or ["-h"] => Usage(0),
    _ => UnknownCommand(),
};

static int Version()
{
    Console.WriteLine(ReDowsInfo.Version);
    return 0;
}

static int Usage(int exitCode)
{
    Console.WriteLine($"{ReDowsInfo.Name} {ReDowsInfo.Version} — pre-reset inventory.");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  redows context show [--json]            Discover and display this machine's scan context.");
    Console.WriteLine("  redows scan [--root <path>] [--rules <dir>] [--out <file>] [--manifest <file>] [--json]");
    Console.WriteLine("                                          Walk and classify (read-only), print the completeness report.");
    Console.WriteLine("                                          --manifest writes one JSONL line per CAPTURE item (the file-by-file keep list).");
    Console.WriteLine("                                          Exit codes: 0 complete, 1 invalid ruleset, 2 usage, 3 interrupted, 4 unexpected error.");
    Console.WriteLine("  redows apps [--out <dir>] [--enrich-winget] [--json]");
    Console.WriteLine("                                          Installed-applications inventory (reinstall list).");
    Console.WriteLine("                                          --enrich-winget RUNS winget on this machine (opt-in side effects).");
    Console.WriteLine("  redows export [--target indows] [--from <apps.json>] [--out <file>]");
    Console.WriteLine("                                          Turn apps.json into an InDows winget catalog (configuration.dsc.yaml).");
    Console.WriteLine("                                          Read-only; reads apps.json (default './apps.json'). Exit: 0 ok, 2 usage, 3 input, 4 error.");
    Console.WriteLine("  redows settings [--out <dir>] [--catalog <dir>] [--json] [--by-module]");
    Console.WriteLine("                                          Read catalogued Windows settings (read-only). --by-module = group by InDows module (profile).");
    Console.WriteLine("                                          Catalog = YAML under 'settings/'. Exit: 0 ok, 1 invalid catalog, 2 usage, 4 error.");
    Console.WriteLine("  redows secrets [--out <dir>] [--catalog <dir>] [--json]");
    Console.WriteLine("                                          Inventory registry-only app secrets/config a file scan misses (read-only, locations only).");
    Console.WriteLine("                                          Headline = the 'export before reset' alert list. Exit: 0 ok, 1 invalid catalog, 2 usage, 4 error.");
    Console.WriteLine("  redows profile --out <dir> [--from <apps.json>] [--catalog <dir>]");
    Console.WriteLine("                                          Write the complete InDows profile folder (apps catalog + settings + README).");
    Console.WriteLine("                                          Read-only; closes the ReDows -> InDows loop. Exit: 0 ok, 1 invalid catalog, 2 usage, 3 input, 4 error.");
    Console.WriteLine("  redows rules validate [--rules <dir>]   Load and validate the ruleset (fail-closed).");
    Console.WriteLine("  redows rules schema [--out <file>]      Emit the generated JSON Schema for ruleset files.");
    Console.WriteLine("  redows --version                        Print the version.");
    return exitCode;
}

static int UnknownCommand()
{
    Console.Error.WriteLine("Unknown command.");
    return Usage(2);
}

static int RulesValidate(string[] options)
{
    if (!TryGetOption(options, "--rules", "rules", out var directory))
    {
        return 2;
    }

    try
    {
        var ruleset = RulesetLoader.LoadDirectory(RulesLocator.Resolve(directory!)); // default is "rules", never null here
        var exceptionCount = ruleset.Rules.Sum(CountExceptions);
        Console.WriteLine(
            $"Ruleset OK — {ruleset.Rules.Count} rules, {exceptionCount} exceptions (schema v{ruleset.SchemaVersion}) in '{directory}'.");
        return 0;
    }
    catch (RulesetValidationException ex)
    {
        Console.Error.WriteLine($"Ruleset INVALID — {ex.Errors.Count} error(s). Refusing to scan (fail-closed).");
        foreach (var error in ex.Errors)
        {
            Console.Error.WriteLine($"  - {error}");
        }

        return 1;
    }

    static int CountExceptions(ReDows.Core.Rules.Rule rule)
    {
        return Count(rule.Exceptions);

        static int Count(IReadOnlyList<ReDows.Core.Rules.RuleException> exceptions) =>
            exceptions.Sum(e => 1 + Count(e.Exceptions));
    }
}

static int RulesSchema(string[] options)
{
    if (!TryGetOption(options, "--out", null, out var outputFile))
    {
        return 2;
    }

    var json = RulesetSchemaGenerator.GenerateJson();
    if (outputFile is null)
    {
        Console.Write(json);
        return 0;
    }

    try
    {
        File.WriteAllText(outputFile, json);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"Unexpected error: cannot write '{outputFile}': {ex.Message}");
        return 4;
    }

    Console.WriteLine($"Schema written to '{outputFile}'.");
    return 0;
}

static bool TryGetOption(string[] options, string name, string? defaultValue, out string? value)
{
    value = defaultValue;
    switch (options)
    {
        case []:
            return true;
        case [var flag, var flagValue] when flag == name:
            value = flagValue;
            return true;
        default:
            Console.Error.WriteLine($"Invalid options. Expected: {name} <value>");
            return false;
    }
}
