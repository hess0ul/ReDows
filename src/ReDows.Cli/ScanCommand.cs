using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ReDows.Core.Rules;
using ReDows.Core.Rules.Loading;
using ReDows.Core.Scanning;
using ReDows.Providers.Windows;
using ReDows.Providers.Windows.Apps;

namespace ReDows.Cli;

/// <summary>
/// 'redows scan' — walk the machine (read-only), classify every object and print
/// the completeness report. Exit codes: 0 complete, 1 invalid ruleset, 2 usage,
/// 3 interrupted (partial report, marked INVALID), 4 unexpected error.
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
        var asJson = false;

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
                case "--json":
                    asJson = true;
                    break;
                default:
                    Console.Error.WriteLine($"Invalid option '{options[i]}'. Usage: redows scan [--root <path>] [--rules <dir>] [--out <file>] [--json]");
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
            Console.Error.WriteLine($"Ruleset INVALID — {ex.Errors.Count} error(s). Refusing to scan (fail-closed).");
            foreach (var error in ex.Errors)
            {
                Console.Error.WriteLine($"  - {error}");
            }

            return 1;
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
                $"App indexes: {indexZones.Zones.Count} claimed zone(s), {indexZones.Notes.Count} note(s).");
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

        try
        {
            var fullOutputPath = outputFile is null ? null : Path.GetFullPath(outputFile);
            var scanOptions = new ScanOptions(
                Roots: root is null ? null : [Path.GetFullPath(root)],
                ExcludedOutputPaths: fullOutputPath is null ? null : [fullOutputPath],
                OnProgress: (items, path) => Console.Error.WriteLine($"  … {items:N0} items — {Sanitize(path)}"),
                ClaimedZones: indexZones.Zones);

            Console.Error.WriteLine(root is null
                ? $"Scanning {windowsContext.Context.Volumes.Count} volume(s)…"
                : $"Scanning subtree '{root}'…");

            var report = ScanEngine.Run(
                ruleset,
                windowsContext.Context,
                new WindowsFileSystemWalker(),
                new WindowsFileSystemView(),
                scanOptions,
                cancellation.Token);

            var rendered = asJson ? RenderJson(report, indexZones) : RenderText(report, windowsContext, indexZones);
            Console.WriteLine(rendered);
            if (fullOutputPath is not null)
            {
                File.WriteAllText(fullOutputPath, rendered);
                Console.Error.WriteLine($"Report written to '{fullOutputPath}'.");
            }

            return report.Partial ? 3 : 0;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    private static string Sanitize(string text) => ConsoleText.Sanitize(text);

    private static string RenderJson(ScanReport report, IndexZoneDiscovery indexZones) =>
        // Envelope: the index-derived zones and their notes (volume-absent
        // ALERTs…) are scan inputs, not ScanReport fields — but they must
        // survive into machine-readable output (forget-nothing).
        JsonSerializer.Serialize(
            new { Report = report, IndexClaimedZones = indexZones.Zones, IndexNotes = indexZones.Notes },
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
            ? "status: PARTIAL — INVALID (interrupted before completion; figures cover only what was walked)"
            : "status: COMPLETE");
        text.AppendLine($"roots: {string.Join("  ", report.Roots)}");
        text.AppendLine(
            $"items seen: {report.TotalItems:N0} ({report.TotalFiles:N0} files, {report.TotalDirectories:N0} directories, " +
            $"{report.TotalUnknownSubtrees:N0} unknown subtrees)");
        text.AppendLine($"bytes seen: {Bytes(report.TotalBytes)} (logical file sizes)");

        text.AppendLine();
        text.AppendLine("== Accounting by verdict ==");
        foreach (var verdict in Enum.GetValues<Verdict>())
        {
            var totals = report.ByVerdict.GetValueOrDefault(verdict, new VerdictTotals(0, 0));
            text.AppendLine($"  {verdict.Format(),-14}: {totals.Items,12:N0} items  {Bytes(totals.Bytes),12}");
        }

        var equation = string.Join(" + ", Enum.GetValues<Verdict>()
            .Select(v => report.ByVerdict.GetValueOrDefault(v, new VerdictTotals(0, 0)).Items));
        var balanced = report.UnaccountedItems == 0;
        text.AppendLine($"  equation: {equation} = {report.AccountedItems:N0} accounted vs {report.TotalItems:N0} seen → " +
            (balanced ? "0 unaccounted ✓" : $"{report.UnaccountedItems:N0} UNACCOUNTED ✗ (engine bug — report invalid)"));

        var engineHits = report.RuleHits.Where(h => h.Stage == EngineRuleIds.Stage).ToList();
        if (engineHits.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("== Engine items (counted, never silent) ==");
            foreach (var hit in engineHits)
            {
                text.AppendLine($"  {hit.RuleId,-24}: {hit.Items,10:N0} items  → {hit.Verdict.Format()}");
            }
        }

        if (report.PreResetAlerts.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("== PRE-RESET ALERTS — DPAPI machine-bound: captured bytes will be UNREADABLE after the reset ==");
            text.AppendLine("   Export or synchronize these BEFORE wiping (the application's own export feature).");
            foreach (var alert in report.PreResetAlerts)
            {
                text.AppendLine($"  {alert.RuleId,-40} {alert.Items,8:N0} item(s)");
            }
        }

        var volumeAbsentNotes = indexZones.Notes.Where(n => n.VolumeAbsent).ToList();
        if (volumeAbsentNotes.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("== INDEX ALERTS — an app index references data on an ABSENT volume ==");
            text.AppendLine("   That data is NOT in this inventory: plug the volume back and re-scan.");
            foreach (var note in volumeAbsentNotes)
            {
                text.AppendLine($"  {Sanitize(note.Id)}: {Sanitize(note.TargetPath)} — {note.Message}");
            }
        }

        if (indexZones.Zones.Count > 0)
        {
            var claimedHits = report.RuleHits
                .Where(h => h.Stage == ScanEngine.ClaimedStage)
                .ToDictionary(h => h.RuleId, StringComparer.Ordinal);
            text.AppendLine();
            text.AppendLine("== Index-claimed zones (INDEX_EXTERNE — app configs pointing at data elsewhere) ==");
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
            text.AppendLine("== Index notes (counted, never silent) ==");
            foreach (var note in otherIndexNotes)
            {
                text.AppendLine($"  {Sanitize(note.Id)}: {Sanitize(note.TargetPath)} — {note.Message}");
            }
        }

        var excludedVolumes = windowsContext.AllVolumes.Where(v => !v.Scannable).ToList();
        if (excludedVolumes.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("== Volumes not scanned (engine.volume_unmounted) ==");
            foreach (var volume in excludedVolumes)
            {
                text.AppendLine($"  {volume.GuidPath} — {volume.ExclusionReason}");
            }
        }

        if (report.UninstantiatedRules.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("== Rules not instantiated (degraded bindings, counted) ==");
            foreach (var rule in report.UninstantiatedRules)
            {
                text.AppendLine($"  {rule.RuleId} for {Sanitize(rule.Binding)}: token '{rule.MissingToken}' unresolved");
            }
        }

        var captures = report.RuleHits
            .Where(h => h.Verdict is Verdict.CaptureConfig or Verdict.CaptureUser or Verdict.CaptureSecret)
            .Take(10)
            .ToList();
        if (captures.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("== Top capture rules (by bytes) ==");
            foreach (var hit in captures)
            {
                text.AppendLine($"  {hit.RuleId,-32} {hit.Verdict.Format(),-14} {hit.Items,10:N0} items  {Bytes(hit.Bytes),12}");
            }
        }

        text.AppendLine();
        text.AppendLine($"== REVIEW rollup — the human work queue (top {report.ReviewRollup.Count} head directories by bytes) ==");
        foreach (var bucket in report.ReviewRollup)
        {
            text.AppendLine($"  {Bytes(bucket.Bytes),12}  {bucket.Items,10:N0} items   {Sanitize(bucket.Directory)}");
        }

        text.AppendLine();
        text.AppendLine("== Declared limits (V1) ==");
        foreach (var limit in report.DeclaredLimits)
        {
            text.AppendLine($"  - {limit}");
        }

        return text.ToString();
    }

    private static string Bytes(long bytes) => bytes switch
    {
        >= 1L << 40 => $"{bytes / (double)(1L << 40):F2} TB",
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F2} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F2} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F1} KB",
        _ => $"{bytes} B",
    };
}
