using System.IO;
using ReDows.Core.Classification;
using ReDows.Core.Duplicates;
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

        var result = Shape(report);

        // Optional second pass: hunt byte-identical files. Read-only. If it is cancelled, the
        // classification result above is still returned — just without a duplicates section.
        if (request.Duplicates is { Enabled: true } duplicateScan)
        {
            var duplicateRoots = request.FolderRoot is null
                ? windowsContext.Context.Volumes.Select(v => v.RootPath).ToList()
                : [Path.GetFullPath(request.FolderRoot)];
            try
            {
                result = result with { Duplicates = FindDuplicates(duplicateRoots, duplicateScan, progress, cancellationToken) };
            }
            catch (OperationCanceledException)
            {
                // keep the classification result, drop the (incomplete) duplicate hunt
            }
        }

        return result;
    }

    private static DuplicateSummary FindDuplicates(
        IReadOnlyList<string> roots, DuplicateScan scan, IProgress<ScanProgress> progress, CancellationToken cancellationToken)
    {
        var extensions = scan.Extensions is null
            ? null
            : new HashSet<string>(scan.Extensions, StringComparer.OrdinalIgnoreCase);

        var walker = new WindowsFileSystemWalker();
        var files = new List<FileRef>();
        foreach (var root in roots)
        {
            foreach (var entry in walker.Walk(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Error is not null || entry.IsDirectory)
                {
                    continue;
                }

                if (extensions is null || extensions.Contains(ExtensionOf(entry.Path)))
                {
                    files.Add(new FileRef(entry.Path, entry.SizeBytes));
                }
            }
        }

        long hashed = 0;
        var groups = DuplicateFinder.Find(files, new Sha256FileHasher(), SafeLastModifiedUtc, minSize: 1, onFullHash: _ =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++hashed % 500 == 0)
            {
                progress.Report(new ScanProgress(hashed, "comparing possible duplicate files…"));
            }
        });

        var top = groups.Take(25)
            .Select(group => new DuplicateGroupRow(
                Format.Bytes(group.ReclaimableBytes),
                Format.Bytes(group.Size),
                group.Count,
                group.Primary.Path,
                group.Locations.Skip(1).Select(location => location.Path).ToList()))
            .ToList();

        return new DuplicateSummary(
            groups.Count,
            groups.Sum(group => group.Count - 1),
            Format.Bytes(groups.Sum(group => group.ReclaimableBytes)),
            top);
    }

    private static string ExtensionOf(string path)
    {
        var name = path.AsSpan();
        var slash = name.LastIndexOfAny('/', '\\');
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }

        var dot = name.LastIndexOf('.');
        return dot >= 0 && dot < name.Length - 1 ? name[(dot + 1)..].ToString().ToLowerInvariant() : "";
    }

    private static DateTime SafeLastModifiedUtc(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
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
