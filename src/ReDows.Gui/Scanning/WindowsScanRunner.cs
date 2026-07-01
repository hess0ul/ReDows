using System.IO;
using ReDows.Core.Classification;
using ReDows.Core.Rules;
using ReDows.Core.Rules.Loading;
using ReDows.Core.Scanning;
using ReDows.Providers.Windows;
using ReDows.Providers.Windows.Apps;

namespace ReDows.Gui.Scanning;

/// <summary>
/// The real scan: loads the ruleset (shipped next to the app), builds this PC's read-only context,
/// and runs the same <see cref="ScanEngine"/> the CLI uses — on a background thread, honoring the
/// cancellation token (Cancel → the engine returns a PARTIAL report) and forwarding progress.
/// </summary>
public sealed class WindowsScanRunner : IScanRunner
{
    public Task<ScanResultView> RunAsync(ScanRequest request, IProgress<ScanProgress> progress, CancellationToken cancellationToken) =>
        Task.Run(() => RunCore(request, progress, cancellationToken), cancellationToken);

    private static ScanResultView RunCore(ScanRequest request, IProgress<ScanProgress> progress, CancellationToken cancellationToken)
    {
        if (request.FolderRoot is { } folder && !Directory.Exists(folder))
        {
            throw new InvalidOperationException($"The folder to scan does not exist: '{folder}'.");
        }

        Ruleset ruleset;
        try
        {
            ruleset = RulesetLoader.LoadDirectory(ResolveRulesDirectory());
        }
        catch (RulesetValidationException ex)
        {
            throw new InvalidOperationException(
                $"The rules are invalid or missing ({ex.Errors.Count} error(s)). The 'rules' folder shipped next to the app may be missing or broken.", ex);
        }

        var windowsContext = WindowsScanContextProvider.Build();
        var indexZones = IndexZoneProvider.Discover(windowsContext.Context);

        var options = new ScanOptions(
            Roots: request.FolderRoot is null ? null : [Path.GetFullPath(request.FolderRoot)],
            OnProgress: (items, path) => progress.Report(new ScanProgress(items, path)),
            ClaimedZones: indexZones.Zones,
            CategoryModules: request.CategoryModules);

        var report = ScanEngine.Run(
            ruleset,
            windowsContext.Context,
            new WindowsFileSystemWalker(),
            new WindowsFileSystemView(),
            options,
            cancellationToken);

        return Shape(report);
    }

    /// <summary>Rules ship next to the executable (distribution); fall back to the working directory (dev).</summary>
    private static string ResolveRulesDirectory()
    {
        var nextToExe = Path.Combine(AppContext.BaseDirectory, "rules");
        return Directory.Exists(nextToExe) ? nextToExe : "rules";
    }

    private static ScanResultView Shape(ScanReport report)
    {
        long Items(Verdict v) => report.ByVerdict.GetValueOrDefault(v, new VerdictTotals(0, 0)).Items;
        long Bytes(Verdict v) => report.ByVerdict.GetValueOrDefault(v, new VerdictTotals(0, 0)).Bytes;

        var keepItems = Items(Verdict.CaptureConfig) + Items(Verdict.CaptureUser) + Items(Verdict.CaptureSecret);
        var keepBytes = Bytes(Verdict.CaptureConfig) + Bytes(Verdict.CaptureUser) + Bytes(Verdict.CaptureSecret);

        var equation = string.Join(" + ", Enum.GetValues<Verdict>()
            .Select(v => report.ByVerdict.GetValueOrDefault(v, new VerdictTotals(0, 0)).Items));
        var equationText = $"{equation} = {report.AccountedItems:N0} accounted vs {report.TotalItems:N0} seen" +
            (report.UnaccountedItems == 0 ? " → 0 unaccounted" : $" → {report.UnaccountedItems:N0} UNACCOUNTED (bug)");

        var alerts = report.PreResetAlerts
            .Select(a => new AlertRow(a.RuleId, $"{a.Items:N0} item(s)"))
            .ToList();

        var topReview = report.ReviewRollup
            .Select(b => new ReviewFolderRow(Format.Bytes(b.Bytes), $"{b.Items:N0} items", b.Directory, b.Bytes))
            .ToList();

        return new ScanResultView(
            Partial: report.Partial,
            KeepText: $"{keepItems:N0} items · {Format.Bytes(keepBytes)}",
            ReviewText: $"{Items(Verdict.Review):N0} items · {Format.Bytes(Bytes(Verdict.Review))}",
            IgnoreText: $"{Items(Verdict.Ignore):N0} items · {Format.Bytes(Bytes(Verdict.Ignore))}",
            NoteText: $"{Items(Verdict.NoteOnly):N0} items",
            Balanced: report.UnaccountedItems == 0,
            EquationText: equationText,
            Alerts: alerts,
            TopReview: topReview);
    }
}
