using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ReDows.Core;
using ReDows.Core.Apps;
using ReDows.Core.Modules;
using ReDows.Core.Rules;
using ReDows.Core.Rules.Loading;
using ReDows.Core.Saves;
using ReDows.Core.Scanning;
using ReDows.Providers.Windows;
using ReDows.Providers.Windows.Apps;
using ReDows.Providers.Windows.Saves;

namespace ReDows.Cli;

/// <summary>
/// 'redows scan': walk the machine (read-only), classify every object and print
/// the report. Exit codes: 0 complete, 1 invalid rules, 2 usage,
/// 3 interrupted (partial report), 4 unexpected error.
/// </summary>
public static class ScanCommand
{
    public static int Run(string[] options)
    {
        try
        {
            return RunCore(options);
        }
        catch (Exception ex)
        {
            // The exit-code contract must hold even on unforeseen failures.
            Console.Error.WriteLine($"Unexpected error: {ex.GetType().Name}: {ex.Message}");
            return 4;
        }
    }

    private static int RunCore(string[] options)
    {
        string rulesDirectory = "rules";
        string? root = null;
        string? outputFile = null;
        string? manifestFile = null;
        var asJson = false;
        var noReinstall = false;
        var useLudusavi = false;
        var moduleActions = new Dictionary<string, ModuleAction>(StringComparer.OrdinalIgnoreCase);

        const string usage = "Usage: redows scan [--root <path>] [--rules <dir>] [--out <file>] [--manifest <file>] [--json] [--no-reinstall] [--ludusavi] [--module <name>=<review|keep|ignore>]";

        for (var i = 0; i < options.Length; i++)
        {
            switch (options[i])
            {
                case "--rules" when i + 1 < options.Length:
                    rulesDirectory = options[++i];
                    break;
                case "--root" when i + 1 < options.Length:
                    root = options[++i];
                    break;
                case "--out" when i + 1 < options.Length:
                    outputFile = options[++i];
                    break;
                case "--manifest" when i + 1 < options.Length:
                    manifestFile = options[++i];
                    break;
                case "--json":
                    asJson = true;
                    break;
                case "--no-reinstall":
                    noReinstall = true;
                    break;
                case "--ludusavi":
                    useLudusavi = true;
                    break;
                case "--module" when i + 1 < options.Length:
                    var spec = options[++i];
                    var eq = spec.IndexOf('=');
                    if (eq <= 0 || eq >= spec.Length - 1)
                    {
                        Console.Error.WriteLine($"--module expects <name>=<action> (got '{spec}'). {usage}");
                        return 2;
                    }

                    var moduleName = spec[..eq].Trim();
                    if (!ModuleActions.TryParse(spec[(eq + 1)..].Trim(), out var moduleAction))
                    {
                        Console.Error.WriteLine($"--module '{moduleName}': action must be review, keep or ignore (got '{spec[(eq + 1)..].Trim()}').");
                        return 2;
                    }

                    moduleActions[moduleName] = moduleAction;
                    break;
                default:
                    Console.Error.WriteLine($"Invalid option '{options[i]}'. {usage}");
                    return 2;
            }
        }

        Ruleset ruleset;
        try
        {
            ruleset = RulesetLoader.LoadDirectory(RulesLocator.Resolve(rulesDirectory));
        }
        catch (RulesetValidationException ex)
        {
            Console.Error.WriteLine($"The rules have {ex.Errors.Count} error(s), so nothing was scanned.");
            foreach (var error in ex.Errors)
            {
                Console.Error.WriteLine($"  - {error}");
            }

            return 1;
        }

        // Category modules (games, media...): the user picks what to do per category,
        // read from modules/. If the folder is absent there are simply no modules; a
        // malformed file stops the scan (a detector the user relies on must work, or
        // say why it can't).
        IReadOnlyList<ModuleDefinition> moduleDefinitions;
        try
        {
            moduleDefinitions = ModuleLoader.LoadDirectory(ModulesLocator.Resolve(ModulesLocator.DefaultDirectory));
        }
        catch (ModuleValidationException ex)
        {
            Console.Error.WriteLine($"The category modules have {ex.Errors.Count} error(s), so nothing was scanned.");
            foreach (var error in ex.Errors)
            {
                Console.Error.WriteLine($"  - {error}");
            }

            return 1;
        }

        foreach (var name in moduleActions.Keys)
        {
            if (!moduleDefinitions.Any(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                var available = moduleDefinitions.Count == 0 ? "none" : string.Join(", ", moduleDefinitions.Select(m => m.Name));
                Console.Error.WriteLine($"--module '{name}' is not a known module (available: {available}).");
                return 2;
            }
        }

        var categoryModules = moduleDefinitions
            .Select(m => m.ToCategoryModule(moduleActions.TryGetValue(m.Name, out var a) ? a : m.DefaultAction))
            .ToList();

        if (moduleDefinitions.Count > 0)
        {
            Console.Error.WriteLine("Category modules: " + string.Join(", ", moduleDefinitions.Select(m =>
                $"{m.Name}={(moduleActions.TryGetValue(m.Name, out var a) ? a : m.DefaultAction).Format()}"))
                + " (keep/ignore act only where the ruleset would review; set with --module <name>=keep|ignore).");
        }

        if (root is not null && !Directory.Exists(root))
        {
            Console.Error.WriteLine($"--root '{root}' is not an existing directory.");
            return 2;
        }

        var windowsContext = WindowsScanContextProvider.Build();

        // INDEX_EXTERNE (§D-15): app indexes claim the data zones they point at
        // BEFORE the walk, so relocated profiles/mail stores/libraries get a
        // review/capture verdict even where the static ruleset has no rule.
        var indexZones = IndexZoneProvider.Discover(windowsContext.Context);
        if (indexZones.Zones.Count > 0 || indexZones.Notes.Count > 0)
        {
            Console.Error.WriteLine(
                $"App indexes: {indexZones.Zones.Count} data location(s) elsewhere, {indexZones.Notes.Count} note(s).");
        }

        // App inventory to reinstall zones: a folder the inventory recognises as an
        // installed app can be downloaded again, so its content is ignored where the
        // ruleset would only review it. Never over a keep, never in a data-named
        // folder. On unless --no-reinstall.
        IReadOnlyList<ReinstallZone> reinstallZones = [];
        IReadOnlyList<AppDataZone> appDataZones = [];
        if (noReinstall)
        {
            Console.Error.WriteLine("App inventory: skipped (--no-reinstall). Installed-app folders and app data are left for review.");
        }
        else
        {
            var inventory = AppInventoryProvider.Build(enrichWithWinget: false);
            reinstallZones = ReinstallZoneBuilder.Build(inventory);

            var profileRoots = windowsContext.Context.Profiles
                .Where(p => p.Environment.ContainsKey("AppData") && p.Environment.ContainsKey("LocalAppData"))
                .Select(p => new AppDataZoneBuilder.ProfileDataRoots(p.Environment["AppData"], p.Environment["LocalAppData"]))
                .ToList();
            appDataZones = AppDataZoneBuilder.Build(inventory, profileRoots)
                .Where(z => Directory.Exists(z.PathPrefix))
                .ToList();

            Console.Error.WriteLine(
                $"App inventory: {inventory.Entries.Count} app(s), {reinstallZones.Count} reinstall zone(s) " +
                $"and {appDataZones.Count} app-data zone(s) (re-downloadable install folders are skipped; " +
                "each app's %AppData% is kept and %LocalAppData% is left for review).");
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true; // finish gracefully: emit the partial report, marked INVALID
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Ctrl+C raced past the end of the scan: nothing left to cancel.
            }
        };
        Console.CancelKeyPress += onCancel;

        // Optional ludusavi game-save catalog (--ludusavi): download it on first use (then read from
        // cache), build capture zones from the per-game save locations, and keep only the folders that
        // exist here. Additive, so it only ever moves a save from review to capture. Its data is
        // PCGamingWiki's (CC BY-NC-SA), never bundled; it is fetched onto this machine. Best-effort: a
        // failure just adds no zones.
        if (useLudusavi)
        {
            Console.Error.WriteLine("Game-save catalog (ludusavi): loading (download on first use, then cached)...");
            try
            {
                using var ludusavi = new WindowsLudusaviSource();
                var load = ludusavi.LoadAsync(forceRefresh: false, cancellation.Token).GetAwaiter().GetResult();
                var saveZones = LudusaviSaveZoneBuilder.Build(load.Manifest, windowsContext.Context)
                    .Where(z => Directory.Exists(z.PathPrefix))
                    .ToList();
                appDataZones = appDataZones.Concat(saveZones).ToList();
                Console.Error.WriteLine(
                    $"Game-save catalog: {load.Status} ({load.Manifest.GameCount:N0} games) → {saveZones.Count} save folder(s) found on this PC (captured where scanned). " +
                    "Save locations from PCGamingWiki (CC BY-NC-SA), via ludusavi.");
                if (load.Detail is not null)
                {
                    Console.Error.WriteLine($"  {load.Detail}");
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Game-save catalog: skipped ({ex.GetType().Name}: {ex.Message}).");
            }
        }

        // The manifest is a ReDows output too: exclude it (and the report) from the
        // scan so the run never inventories its own files (engine.self_output).
        var fullOutputPath = outputFile is null ? null : Path.GetFullPath(outputFile);
        var fullManifestPath = manifestFile is null ? null : Path.GetFullPath(manifestFile);
        var excludedOutputs = new List<string>();
        if (fullOutputPath is not null) excludedOutputs.Add(fullOutputPath);
        if (fullManifestPath is not null) excludedOutputs.Add(fullManifestPath);

        StreamWriter? manifestWriter = null;
        var manifestLines = 0L;
        try
        {
            Action<ManifestEntry>? onCapture = null;
            if (fullManifestPath is not null)
            {
                manifestWriter = new StreamWriter(fullManifestPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                onCapture = entry =>
                {
                    manifestWriter.WriteLine(ManifestLine.Format(entry));
                    manifestLines++;
                };
            }

            var scanOptions = new ScanOptions(
                Roots: root is null ? null : [Path.GetFullPath(root)],
                ExcludedOutputPaths: excludedOutputs.Count > 0 ? excludedOutputs : null,
                OnProgress: (items, path) => Console.Error.WriteLine($"  {items:N0} items   {Sanitize(path)}"),
                ClaimedZones: indexZones.Zones,
                OnCapture: onCapture,
                ReinstallZones: reinstallZones,
                AppDataZones: appDataZones,
                CategoryModules: categoryModules);

            Console.Error.WriteLine(root is null
                ? $"Scanning {windowsContext.Context.Volumes.Count} drive(s)..."
                : $"Scanning folder '{root}'...");

            var report = ScanEngine.Run(
                ruleset,
                windowsContext.Context,
                new WindowsFileSystemWalker(),
                new WindowsFileSystemView(),
                scanOptions,
                cancellation.Token);

            var rendered = asJson ? RenderJson(report, indexZones, reinstallZones, appDataZones, categoryModules) : RenderText(report, windowsContext, indexZones);
            Console.WriteLine(rendered);
            if (fullOutputPath is not null)
            {
                File.WriteAllText(fullOutputPath, rendered);
                Console.Error.WriteLine($"Saved the report to '{fullOutputPath}'.");
            }

            if (manifestWriter is not null)
            {
                manifestWriter.Flush();
                // Show the check instead of asking to be trusted: the manifest holds
                // exactly the items the report counts as CAPTURE.
                var captureItems = report.ByVerdict.Where(v => v.Key.IsCapture()).Sum(v => v.Value.Items);
                var balanced = manifestLines == captureItems;
                Console.Error.WriteLine(
                    $"Manifest: {manifestLines:N0} capture line(s) in '{fullManifestPath}' " +
                    (balanced
                        ? $"= {captureItems:N0} capture items in the report ✓"
                        : $"vs {captureItems:N0} capture items ✗ (bug: the counts should match)"));
            }

            return report.Partial ? 3 : 0;
        }
        finally
        {
            manifestWriter?.Dispose();
            Console.CancelKeyPress -= onCancel;
        }
    }

    private static string Sanitize(string text) => ConsoleText.Sanitize(text);

    private static string RenderJson(
        ScanReport report, IndexZoneDiscovery indexZones,
        IReadOnlyList<ReinstallZone> reinstallZones, IReadOnlyList<AppDataZone> appDataZones,
        IReadOnlyList<CategoryModule> categoryModules) =>
        // Envelope: the index-derived zones, their notes (missing-drive alerts),
        // the app-inventory reinstall / app-data zones and the category modules are
        // scan inputs, not ScanReport fields, but they must survive into the JSON output.
        JsonSerializer.Serialize(
            new { Report = report, IndexClaimedZones = indexZones.Zones, IndexNotes = indexZones.Notes, ReinstallZones = reinstallZones, AppDataZones = appDataZones, CategoryModules = categoryModules },
            new JsonSerializerOptions
            {
                WriteIndented = true,
                Converters = { new JsonStringEnumConverter() },
            });

    private static string RenderText(ScanReport report, WindowsContext windowsContext, IndexZoneDiscovery indexZones)
    {
        var text = new StringBuilder();

        text.AppendLine("== ReDows scan report ==");
        text.AppendLine(report.Partial
            ? "status: incomplete (stopped early; the numbers below cover only what was scanned)"
            : "status: complete");
        text.AppendLine($"roots: {string.Join("  ", report.Roots)}");
        text.AppendLine(
            $"items seen: {report.TotalItems:N0} ({report.TotalFiles:N0} files, {report.TotalDirectories:N0} folders, " +
            $"{report.TotalUnknownSubtrees:N0} unreadable folders)");
        text.AppendLine($"bytes seen: {Bytes(report.TotalBytes)} (logical file sizes)");

        text.AppendLine();
        text.AppendLine("== Totals by category ==");
        foreach (var verdict in Enum.GetValues<Verdict>())
        {
            var totals = report.ByVerdict.GetValueOrDefault(verdict, new VerdictTotals(0, 0));
            text.AppendLine($"  {verdict.Format(),-14}: {totals.Items,12:N0} items  {Bytes(totals.Bytes),12}");
        }

        var balanced = report.UnaccountedItems == 0;
        text.AppendLine($"  check: {report.AccountingEquation} " +
            (balanced ? "(0 missed) ✓" : $"({report.UnaccountedItems:N0} missed) ✗ (bug: the report is off)"));

        var engineHits = report.RuleHits.Where(h => h.Stage == EngineRuleIds.Stage).ToList();
        if (engineHits.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("== Decided by the app ==");
            foreach (var hit in engineHits)
            {
                text.AppendLine($"  {hit.RuleId,-24}: {hit.Items,10:N0} items  → {hit.Verdict.Format()}");
            }
        }

        if (report.PreResetAlerts.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("== Before you reset ==");
            text.AppendLine("   These items are locked to this PC and won't open after a reset. Export them first.");
            foreach (var alert in report.PreResetAlerts)
            {
                text.AppendLine($"  {alert.RuleId,-40} {alert.Items,8:N0} item(s)");
            }
        }

        var volumeAbsentNotes = indexZones.Notes.Where(n => n.VolumeAbsent).ToList();
        if (volumeAbsentNotes.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("== Missing drive ==");
            text.AppendLine("   An app points to data on a drive that isn't connected. Reconnect it and scan again.");
            foreach (var note in volumeAbsentNotes)
            {
                text.AppendLine($"  {Sanitize(note.Id)}: {Sanitize(note.TargetPath)}   {note.Message}");
            }
        }

        if (indexZones.Zones.Count > 0)
        {
            var claimedHits = report.RuleHits
                .Where(h => h.Stage == ScanEngine.ClaimedStage)
                .ToDictionary(h => h.RuleId, StringComparer.Ordinal);
            text.AppendLine();
            text.AppendLine("== App data stored elsewhere ==");
            foreach (var group in indexZones.Zones.GroupBy(z => z.Id, StringComparer.Ordinal))
            {
                var hit = claimedHits.GetValueOrDefault(group.Key);
                text.AppendLine($"  {Sanitize(group.Key)} → {hit?.Items ?? 0:N0} items  {Bytes(hit?.Bytes ?? 0)}");
                foreach (var zone in group)
                {
                    text.AppendLine($"      {zone.Verdict.Format(),-14} {Sanitize(zone.PathPrefix)}");
                }
            }
        }

        var otherIndexNotes = indexZones.Notes.Where(n => !n.VolumeAbsent).ToList();
        if (otherIndexNotes.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("== Notes from app configs ==");
            foreach (var note in otherIndexNotes)
            {
                text.AppendLine($"  {Sanitize(note.Id)}: {Sanitize(note.TargetPath)}   {note.Message}");
            }
        }

        var excludedVolumes = windowsContext.AllVolumes.Where(v => !v.Scannable).ToList();
        if (excludedVolumes.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("== Drives not scanned ==");
            foreach (var volume in excludedVolumes)
            {
                text.AppendLine($"  {volume.GuidPath}   {volume.ExclusionReason}");
            }
        }

        if (report.UninstantiatedRules.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("== Rules that couldn't run ==");
            foreach (var rule in report.UninstantiatedRules)
            {
                text.AppendLine($"  {rule.RuleId} for {Sanitize(rule.Binding)}: couldn't resolve '{rule.MissingToken}'");
            }
        }

        var captures = report.RuleHits
            .Where(h => h.Verdict is Verdict.CaptureConfig or Verdict.CaptureUser or Verdict.CaptureSecret)
            .Take(10)
            .ToList();
        if (captures.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("== Biggest items kept (by size) ==");
            foreach (var hit in captures)
            {
                text.AppendLine($"  {hit.RuleId,-32} {hit.Verdict.Format(),-14} {hit.Items,10:N0} items  {Bytes(hit.Bytes),12}");
            }
        }

        var reinstallHits = report.RuleHits.Where(h => h.Stage == ScanEngine.ReinstallStage).ToList();
        if (reinstallHits.Count > 0)
        {
            var items = reinstallHits.Sum(h => h.Items);
            var bytes = reinstallHits.Sum(h => h.Bytes);
            text.AppendLine();
            text.AppendLine(
                $"== Re-installable apps (skipped: {items:N0} items, {Bytes(bytes)}) ==");
            text.AppendLine("   User data inside them (config, saves, and anything already kept) was kept or left for review.");
            foreach (var hit in reinstallHits.Take(15))
            {
                text.AppendLine($"  {Bytes(hit.Bytes),12}  {hit.Items,10:N0} items   {Sanitize(hit.RuleId)}");
            }
        }

        var appDataHits = report.RuleHits.Where(h => h.Stage == ScanEngine.AppDataStage).ToList();
        if (appDataHits.Count > 0)
        {
            var kept = appDataHits.Where(h => h.Verdict.IsCapture()).ToList();
            var surfaced = appDataHits.Where(h => h.Verdict == Verdict.Review).ToList();
            text.AppendLine();
            text.AppendLine(
                $"== App data ({kept.Sum(h => h.Items):N0} items kept ({Bytes(kept.Sum(h => h.Bytes))}), " +
                $"{surfaced.Sum(h => h.Items):N0} left for review) ==");
            text.AppendLine("   Each app's %AppData% is kept. Its %LocalAppData% (config mixed with cache) is left for review.");
            foreach (var hit in appDataHits.OrderByDescending(h => h.Bytes).Take(15))
            {
                text.AppendLine($"  {hit.Verdict.Format(),-14} {Bytes(hit.Bytes),12}  {hit.Items,10:N0} items   {Sanitize(hit.RuleId)}");
            }
        }

        var moduleHits = report.RuleHits.Where(h => h.Stage == ScanEngine.ModuleStage).ToList();
        if (moduleHits.Count > 0)
        {
            var kept = moduleHits.Where(h => h.Verdict.IsCapture()).ToList();
            var dropped = moduleHits.Where(h => h.Verdict == Verdict.Ignore).ToList();
            text.AppendLine();
            text.AppendLine(
                $"== Categories you chose ({kept.Sum(h => h.Items):N0} items kept ({Bytes(kept.Sum(h => h.Bytes))}), " +
                $"{dropped.Sum(h => h.Items):N0} ignored ({Bytes(dropped.Sum(h => h.Bytes))})) ==");
            text.AppendLine("   Applied only where the item was up for review. Under 'ignore', save folders were still left for review.");
            foreach (var hit in moduleHits.OrderByDescending(h => h.Bytes))
            {
                text.AppendLine($"  {hit.Verdict.Format(),-14} {Bytes(hit.Bytes),12}  {hit.Items,10:N0} items   {Sanitize(hit.RuleId)}");
            }
        }

        text.AppendLine();
        text.AppendLine($"== To review (biggest folders first, top {report.ReviewRollup.Count}) ==");
        foreach (var bucket in report.ReviewRollup)
        {
            text.AppendLine($"  {Bytes(bucket.Bytes),12}  {bucket.Items,10:N0} items   {Sanitize(bucket.Directory)}");
        }

        text.AppendLine();
        text.AppendLine("== Known limits ==");
        foreach (var limit in report.DeclaredLimits)
        {
            text.AppendLine($"  - {limit}");
        }

        return text.ToString();
    }

    // Byte sizes come from ReDows.Core.Format so the CLI and GUI never drift.
    private static string Bytes(long bytes) => Format.Bytes(bytes);
}
