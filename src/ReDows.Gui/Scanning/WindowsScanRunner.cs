using System.IO;
using System.Text;
using ReDows.Core.Apps;
using ReDows.Core.Classification;
using ReDows.Core.Duplicates;
using ReDows.Core.Memory;
using ReDows.Core.Rules;
using ReDows.Core.Rules.Loading;
using ReDows.Core.Saves;
using ReDows.Core.Scanning;
using ReDows.Providers.Windows;
using ReDows.Providers.Windows.Apps;
using ReDows.Providers.Windows.Saves;

namespace ReDows.Gui.Scanning;

/// <summary>
/// The real scan: loads the ruleset (shipped next to the app), builds this PC's read-only context,
/// and runs the same <see cref="ScanEngine"/> the CLI uses, on a background thread, honoring the
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

        // App inventory → reinstall + app-data zones (same as the CLI, on by default): a directory the
        // inventory recognises as an installed app is re-acquirable, so its re-downloadable content is
        // ignored where the ruleset would only REVIEW; each app's %AppData% is kept and %LocalAppData%
        // surfaced. Acts ONLY over a review verdict, never a keep/secret. Fail-safe: if the inventory
        // cannot be built for any reason, the scan still runs, just without this recognition.
        IReadOnlyList<ReinstallZone> reinstallZones = [];
        IReadOnlyList<AppDataZone> appDataZones = [];
        var appCount = 0;
        var recognized = false;
        InstalledAppFolders? appFolders = null; // app-made-folder recogniser for the recognized-places briefing
        if (request.RecognizeInstalledApps)
        {
            try
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

                appFolders = new InstalledAppFolders(inventory.Entries
                    .Where(e => e.Kind == AppEntryKind.App)
                    .Select(e => (e.Name, e.Publisher)));

                appCount = inventory.Entries.Count;
                recognized = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // Recognition is a best-effort enhancement: never let it fail the whole scan.
                reinstallZones = [];
                appDataZones = [];
                appFolders = null;
                recognized = false;
            }
        }

        // The shipped folder memory (memory/ next to the app): the source of the recognized-places notes.
        // Best-effort: a missing/broken memory just means fewer recognised places (never a scan failure).
        var memory = new WindowsMemoryCatalog().Load();
        var recognizeZone = BuildZoneRecognizer(memory, appFolders);

        // Optional ludusavi game-save catalog: download (first use) or read from cache the per-game save
        // locations, build capture zones and keep only the folders that actually exist on this PC. Additive
        // like the app-data zones (review -> capture, never a loss). Its data is PCGamingWiki's (CC BY-NC-SA),
        // never bundled; it is fetched onto this machine. Best-effort: a failure just adds no zones.
        IReadOnlyList<AppDataZone> saveZones = [];
        GameSavesImpact? gameSaves = null;
        if (request.UseGameSaveCatalog)
        {
            try
            {
                progress.Report(new ScanProgress(0, "Downloading the game-save catalog (ludusavi)..."));
                var load = new WindowsLudusaviSource().LoadAsync(forceRefresh: false, cancellationToken).GetAwaiter().GetResult();
                saveZones = LudusaviSaveZoneBuilder.Build(load.Manifest, windowsContext.Context)
                    .Where(z => Directory.Exists(z.PathPrefix))
                    .ToList();
                gameSaves = new GameSavesImpact(saveZones.Count, GameSaveSourceText(load.Status), SaveCatalogAttribution);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // the user cancelled during the download; let the scan report "Cancelled"
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                saveZones = [];
                gameSaves = new GameSavesImpact(0, "The game-save catalog could not be used.", SaveCatalogAttribution);
            }
        }

        // Write the backup-candidate items to an app-managed manifest as they are classified: the seed
        // of the "scan + decisions" session file, and the input the Backup screen copies. Both CAPTURE
        // (auto-kept: config / user / secret) and REVIEW (the "human decides" pile) are recorded, with
        // their verdicts, so the backup can copy the user's kept-minus-trash selection while a secret
        // still routes to the vault. If it cannot be written, the scan still runs; there is simply no
        // manifest to back up (best-effort).
        var manifestPath = ResolveManifestPath();
        StreamWriter? manifestWriter = TryOpenManifest(manifestPath);
        var wroteManifest = manifestWriter is not null;

        var options = new ScanOptions(
            Roots: request.FolderRoot is null ? null : [Path.GetFullPath(request.FolderRoot)],
            OnProgress: (items, path) => progress.Report(new ScanProgress(items, path)),
            ClaimedZones: indexZones.Zones,
            OnCapture: manifestWriter is null ? null : entry => manifestWriter.WriteLine(ManifestLine.Format(entry)),
            OnReview: manifestWriter is null ? null : entry => manifestWriter.WriteLine(ManifestLine.Format(entry)),
            ReinstallZones: reinstallZones,
            AppDataZones: saveZones.Count == 0 ? appDataZones : appDataZones.Concat(saveZones).ToList(),
            CategoryModules: request.CategoryModules,
            RecognizeZone: recognizeZone);

        ScanReport report;
        try
        {
            report = ScanEngine.Run(
                ruleset,
                windowsContext.Context,
                new WindowsFileSystemWalker(),
                new WindowsFileSystemView(),
                options,
                cancellationToken);
        }
        finally
        {
            manifestWriter?.Flush();
            manifestWriter?.Dispose();
        }

        var result = Shape(report) with { ManifestPath = wroteManifest ? manifestPath : null };
        if (recognized)
        {
            result = result with { InstalledApps = InstalledAppsImpactOf(report, appCount) };
        }

        if (gameSaves is not null)
        {
            result = result with { GameSaves = gameSaves };
        }

        // Optional second pass: hunt byte-identical files. Read-only. If it is cancelled, the
        // classification result above is still returned, just without a duplicates section.
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
        // A caching hasher: it records every full hash so the backup's own de-duplication can reuse them
        // instead of reading the same files again (a file's size + last-write time gate the reuse).
        var hasher = new CachingFileHasher(new Sha256FileHasher(), FileTimes.SafeSize, FileTimes.SafeLastModifiedUtc);
        var groups = DuplicateFinder.Find(files, hasher, FileTimes.SafeLastModifiedUtc, minSize: 1, onFullHash: _ =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++hashed % 500 == 0)
            {
                progress.Report(new ScanProgress(hashed, "comparing possible duplicate files..."));
            }
        });
        HashCache.Write(ResolveHashCachePath(), hasher.Entries);

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

    /// <summary>The app-managed capture manifest of the last scan (the Backup screen's default input).</summary>
    private static string ResolveManifestPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ReDows",
            "last-scan.jsonl");

    private static string ResolveHashCachePath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ReDows",
            HashCache.FileName);

    /// <summary>Open the manifest for writing (best-effort): a write failure just means no backup seed.</summary>
    private static StreamWriter? TryOpenManifest(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>Rules ship next to the executable (distribution); fall back to the working directory (dev).</summary>
    private static string ResolveRulesDirectory()
    {
        var nextToExe = Path.Combine(AppContext.BaseDirectory, "rules");
        return Directory.Exists(nextToExe) ? nextToExe : "rules";
    }

    /// <summary>
    /// Compose the end-of-scan recogniser from the two knowledge sources: the shipped folder memory
    /// (AppData, .ssh, Steam, node_modules...) first, then this PC's installed apps (a folder named like an
    /// app or its publisher, such as ShareX or Adobe). Returns null when neither loaded, so the scan simply skips
    /// the briefing. Metadata-only: the recogniser is handed a directory NAME and PATH, never a file.
    /// </summary>
    private static Func<string, string, Verdict, RecognizedZoneInfo?>? BuildZoneRecognizer(
        FolderMemory? memory, InstalledAppFolders? appFolders)
    {
        if (memory is null && (appFolders is null || appFolders.Count == 0))
        {
            return null;
        }

        return (name, _, verdict) =>
        {
            if (memory?.DescribeName(name) is { } known)
            {
                return new RecognizedZoneInfo(
                    "mem:" + known.Match.ToLowerInvariant(),
                    CleanLabel(known.Match),
                    known.Note.Trim(),
                    known.Importance ?? ScanMemory.ImportanceOf(verdict)); // memory's colour, else the scan verdict's
            }

            if (appFolders?.Match(name) is { } app)
            {
                return new RecognizedZoneInfo(
                    "app:" + app.ToLowerInvariant(),
                    app,
                    $"Looks like {app}'s data folder, made by the app, not by you. Keep what you exported or created inside; the app recreates the rest.",
                    "maybe");
            }

            return null;
        };
    }

    /// <summary>A memory match pattern as a friendly label: "OneDrive*" → "OneDrive", "Visual Studio*" → "Visual Studio".</summary>
    private static string CleanLabel(string match)
    {
        var trimmed = match.Trim().TrimEnd('*').Trim();
        return trimmed.Length == 0 ? match.Trim() : trimmed;
    }

    /// <summary>The licence-required credit for the game-save locations (PCGamingWiki is CC BY-NC-SA).</summary>
    private const string SaveCatalogAttribution = "Save locations from PCGamingWiki (CC BY-NC-SA), via the ludusavi project.";

    private static string GameSaveSourceText(LudusaviSourceStatus status) => status switch
    {
        LudusaviSourceStatus.Downloaded => "Downloaded the ludusavi save catalog.",
        LudusaviSourceStatus.Cached => "Using the ludusavi save catalog already on this PC.",
        _ => "Could not download the save catalog (offline?), so none were added.",
    };

    /// <summary>
    /// What recognizing installed apps did, read straight off the engine's own counted rule hits:
    /// the reinstall stage = install folders ignored as re-downloadable; the app-data stage splits
    /// into config kept (a capture verdict) and local data surfaced for review.
    /// </summary>
    private static InstalledAppsImpact InstalledAppsImpactOf(ScanReport report, int appCount)
    {
        static string Line(long items, long bytes) => $"{Format.Bytes(bytes)} · {items:N0} items";

        var reinstall = report.RuleHits.Where(h => h.Stage == ScanEngine.ReinstallStage).ToList();
        var appData = report.RuleHits.Where(h => h.Stage == ScanEngine.AppDataStage).ToList();
        var kept = appData.Where(h => h.Verdict.IsCapture()).ToList();
        var surfaced = appData.Where(h => h.Verdict == Verdict.Review).ToList();

        return new InstalledAppsImpact(
            appCount,
            Line(reinstall.Sum(h => h.Items), reinstall.Sum(h => h.Bytes)),
            Line(kept.Sum(h => h.Items), kept.Sum(h => h.Bytes)),
            Line(surfaced.Sum(h => h.Items), surfaced.Sum(h => h.Bytes)));
    }

    private static ScanResultView Shape(ScanReport report)
    {
        long Items(Verdict v) => report.ByVerdict.GetValueOrDefault(v, new VerdictTotals(0, 0)).Items;
        long Bytes(Verdict v) => report.ByVerdict.GetValueOrDefault(v, new VerdictTotals(0, 0)).Bytes;

        var keepItems = Items(Verdict.CaptureConfig) + Items(Verdict.CaptureUser) + Items(Verdict.CaptureSecret);
        var keepBytes = Bytes(Verdict.CaptureConfig) + Bytes(Verdict.CaptureUser) + Bytes(Verdict.CaptureSecret);

        var equationText = report.AccountingEquation +
            (report.UnaccountedItems == 0 ? " (0 missed)" : $" ({report.UnaccountedItems:N0} missed)");

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
            TopReview: topReview,
            RecognizedZones: report.RecognizedZones
                .Select(z => new RecognizedZoneRow(z.Importance, z.Label, z.Count > 1 ? $"×{z.Count:N0}" : "", z.Note))
                .ToList());
    }
}
