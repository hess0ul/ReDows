using ReDows.Core.Classification;
using ReDows.Core.Rules;
using ReDows.Core.Rules.Globbing;

namespace ReDows.Core.Scanning;

// inside the namespace so the alias beats the ReDows.Core.Classification namespace lookup
using Classification = ReDows.Core.Classification.Classification;

/// <summary>
/// Drives a scan: walks every root, gives each streamed entry exactly one verdict
/// (engine overrides first, then the ruleset, then default REVIEW) and aggregates
/// the completeness report. Pure: all I/O lives behind IFileSystemWalker and
/// IFileSystemView, so the whole engine is testable on a fake tree.
/// </summary>
public static class ScanEngine
{
    /// <summary>Stage name of index-claimed zone verdicts in rule hits (INDEX_EXTERNE §D-15).</summary>
    public const string ClaimedStage = "claimed";

    /// <summary>Stage name of app-inventory reinstall-zone ignores in rule hits (app-zones increment 3).</summary>
    public const string ReinstallStage = "reinstall";

    /// <summary>Stage name of app-inventory data-zone keeps in rule hits (app-zones increment 4).</summary>
    public const string AppDataStage = "appdata";

    /// <summary>Stage name of user-selected category-module verdicts in rule hits (games, media…).</summary>
    public const string ModuleStage = "module";

    /// <summary>Aggregation depth of the REVIEW rollup, in segments below each root.</summary>
    private const int ReviewBucketDepth = 2;

    private const int ReviewRollupSize = 25;

    public static ScanReport Run(
        Ruleset ruleset,
        ScanContext context,
        IFileSystemWalker walker,
        IFileSystemView fileSystem,
        ScanOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new ScanOptions();
        if (options.ProgressInterval <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "ProgressInterval must be positive");
        }

        var classifier = new Classifier(ruleset, context, fileSystem);

        var roots = (options.Roots is { Count: > 0 }
            ? options.Roots
            : context.Volumes.Select(v => v.RootPath).ToList())
            .Select(ScanPaths.Normalize)
            .ToList();

        var excludedOutputs = (options.ExcludedOutputPaths ?? []).Select(ScanPaths.Normalize).ToList();
        foreach (var excluded in excludedOutputs)
        {
            // Self-output is the only engine-level ignore: a too-shallow prefix
            // (volume root, bare drive) would silently self-ignore a whole tree.
            if (ScanPaths.Split(excluded).Length < 2)
            {
                throw new ArgumentException(
                    $"excluded output path '{excluded}' is too shallow — it would self-ignore a whole volume", nameof(options));
            }
        }

        var orphanRoots = context.Orphans.Select(ScanPaths.Normalize).ToList();
        var claimedZones = (options.ClaimedZones ?? [])
            .Select(z => (Zone: z, Prefix: ScanPaths.Normalize(z.PathPrefix)))
            .ToList();
        // Safety net (forget-nothing): a reinstall zone may ignore, so it must never
        // sweep a user profile or a captured Known Folder. Drop any zone that
        // CONTAINS one (a bogus InstallLocation pointing at a profile root would
        // otherwise ignore un-captured AppData). A legit per-app install dir
        // contains no such root, so it passes untouched.
        var protectedRoots = context.Profiles
            .SelectMany(p => p.KnownFolders.Values.Append(p.RootPath))
            .Select(ScanPaths.Normalize)
            .ToList();
        var reinstallZones = (options.ReinstallZones ?? [])
            .Select(z => (Zone: z, Prefix: ScanPaths.Normalize(z.PathPrefix)))
            .Where(z => !protectedRoots.Exists(root => IsUnder(root, z.Prefix)))
            .ToList();
        var appDataZones = (options.AppDataZones ?? [])
            .Select(z => (Zone: z, Prefix: ScanPaths.Normalize(z.PathPrefix)))
            .ToList();
        // Category modules act only over a REVIEW verdict and honour the save-name
        // net under ignore, so (unlike a bogus reinstall zone) they need no
        // profile-root guard: an explicit "ignore games" is the user's own choice.
        var categoryModules = options.CategoryModules ?? [];

        long files = 0, directories = 0, unknownSubtrees = 0, bytes = 0, items = 0;
        var byVerdict = new Dictionary<Verdict, (long Items, long Bytes)>();
        var ruleHits = new Dictionary<string, (string Stage, Verdict Verdict, long Items, long Bytes)>(StringComparer.Ordinal);
        var reviewBuckets = new Dictionary<string, (long Items, long Bytes)>(StringComparer.OrdinalIgnoreCase);
        var alerts = new Dictionary<string, long>(StringComparer.Ordinal);
        var partial = false;

        foreach (var root in roots)
        {
            var bucketDepth = ScanPaths.Split(root).Length + ReviewBucketDepth;

            foreach (var entry in walker.Walk(root))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    partial = true;
                    break;
                }

                // Split once per entry: classification, override checks and the
                // review bucket all reuse these segments (hot path, 1M+ items).
                var segments = ScanPaths.Split(entry.Path);
                var path = string.Join('/', segments);
                var entryBytes = entry.IsDirectory || entry.Error is not null ? 0 : entry.SizeBytes;

                items++;
                bytes += entryBytes;
                if (entry.Error is not null)
                {
                    unknownSubtrees++;
                }
                else if (entry.IsDirectory)
                {
                    directories++;
                }
                else
                {
                    files++;
                }

                var classification = Classify(classifier, entry, path, segments, excludedOutputs, orphanRoots, claimedZones, reinstallZones, appDataZones, categoryModules);
                if (classification.HasFlag(RuleFlag.DpapiMachineBound))
                {
                    alerts[classification.RuleId] = alerts.GetValueOrDefault(classification.RuleId) + 1;
                }

                var verdictTotals = byVerdict.GetValueOrDefault(classification.Verdict);
                byVerdict[classification.Verdict] = (verdictTotals.Items + 1, verdictTotals.Bytes + entryBytes);

                // Per-item manifest sink: one call per CAPTURE item, alongside the
                // tally above, so the manifest line count matches the report exactly.
                if (options.OnCapture is not null && classification.Verdict.IsCapture())
                {
                    options.OnCapture(ManifestEntry.From(path, classification, entryBytes, entry.IsDirectory));
                }

                var hit = ruleHits.GetValueOrDefault(classification.RuleId, (classification.Stage, classification.Verdict, 0, 0));
                ruleHits[classification.RuleId] = (hit.Stage, hit.Verdict, hit.Items + 1, hit.Bytes + entryBytes);

                if (classification.Verdict == Verdict.Review)
                {
                    // Per-item REVIEW sink (the GUI's backup-candidate manifest): one call per review item.
                    options.OnReview?.Invoke(ManifestEntry.From(path, classification, entryBytes, entry.IsDirectory));

                    // Aggregate under an ancestor directory: never deeper than the item's parent.
                    var bucket = string.Join('/', segments.Take(Math.Min(Math.Max(segments.Length - 1, 1), bucketDepth)));
                    var bucketTotals = reviewBuckets.GetValueOrDefault(bucket);
                    reviewBuckets[bucket] = (bucketTotals.Items + 1, bucketTotals.Bytes + entryBytes);
                }

                if (options.OnProgress is not null && items % options.ProgressInterval == 0)
                {
                    options.OnProgress(items, path);
                }
            }

            if (partial)
            {
                break;
            }
        }

        return new ScanReport(
            Partial: partial,
            Roots: roots,
            TotalItems: items,
            TotalFiles: files,
            TotalDirectories: directories,
            TotalUnknownSubtrees: unknownSubtrees,
            TotalBytes: bytes,
            ByVerdict: byVerdict.ToDictionary(p => p.Key, p => new VerdictTotals(p.Value.Items, p.Value.Bytes)),
            RuleHits: ruleHits
                .Select(p => new RuleHit(p.Key, p.Value.Stage, p.Value.Verdict, p.Value.Items, p.Value.Bytes))
                .OrderByDescending(h => h.Bytes)
                .ThenByDescending(h => h.Items)
                .ToList(),
            ReviewRollup: reviewBuckets
                .Select(p => new ReviewBucket(p.Key, p.Value.Items, p.Value.Bytes))
                .OrderByDescending(b => b.Bytes)
                .ThenByDescending(b => b.Items)
                .Take(ReviewRollupSize)
                .ToList(),
            UninstantiatedRules: classifier.UninstantiatedRules,
            PreResetAlerts: alerts
                .Select(p => new PreResetAlert(p.Key, p.Value))
                .OrderByDescending(a => a.Items)
                .ToList(),
            DeclaredLimits: ScanReport.V1Limits);
    }

    /// <summary>
    /// Engine overrides come before the ruleset, most defensive first: an
    /// unenumerable directory is an unknown subtree (REVIEW); ReDows' own output is
    /// the one explicit engine whitelist; an orphan profile tree is user data off
    /// the radar, so no ignore rule may touch it; a non-traversed reparse DIRECTORY
    /// (and a name-surrogate file: symlink) is a pointer whose target lives — and
    /// is scanned — elsewhere. A reparse-tagged file that is NOT a name surrogate
    /// (WOF compression, deduplication…) is real user data: it flows to the ruleset.
    /// </summary>
    private static Classification Classify(
        Classifier classifier,
        ScanEntry entry,
        string path,
        string[] segments,
        IReadOnlyList<string> excludedOutputs,
        IReadOnlyList<string> orphanRoots,
        IReadOnlyList<(ClaimedZone Zone, string Prefix)> claimedZones,
        IReadOnlyList<(ReinstallZone Zone, string Prefix)> reinstallZones,
        IReadOnlyList<(AppDataZone Zone, string Prefix)> appDataZones,
        IReadOnlyList<CategoryModule> categoryModules)
    {
        if (entry.Error is not null)
        {
            return new Classification(Verdict.Review, EngineRuleIds.Inaccessible, EngineRuleIds.Stage);
        }

        if (IsUnderAny(path, excludedOutputs))
        {
            return new Classification(Verdict.Ignore, EngineRuleIds.SelfOutput, EngineRuleIds.Stage);
        }

        if (IsUnderAny(path, orphanRoots))
        {
            return new Classification(Verdict.Review, EngineRuleIds.OrphanProfile, EngineRuleIds.Stage);
        }

        if (entry.ReparseTag != 0 && !ReparsePoints.ShouldTraverse(entry.ReparseTag))
        {
            if (ReparsePoints.IsNameSurrogate(entry.ReparseTag))
            {
                // Symlink/junction/appexeclink: a pointer whose target lives —
                // and is scanned — elsewhere. A note is enough.
                return new Classification(Verdict.NoteOnly, EngineRuleIds.ReparsePoint, EngineRuleIds.Stage);
            }

            if (entry.IsDirectory)
            {
                // Non-surrogate, non-traversed directory (ProjFS/WCI root,
                // unknown 0x9000xxxx tag, unreadable tag): its contents are
                // unique and were never walked — a blind spot, surfaced as
                // REVIEW, never as a benign note (forget-nothing).
                return new Classification(Verdict.Review, EngineRuleIds.UnknownReparse, EngineRuleIds.Stage);
            }

            // Non-surrogate FILE (WOF-compressed, deduplicated…): real data — ruleset.
        }

        // Claimed zones (index-derived, review/capture only): the most specific
        // claim wins — longest prefix, then the most conservative verdict.
        ClaimedZone? bestZone = null;
        var bestLength = -1;
        foreach (var (zone, prefix) in claimedZones)
        {
            if (!IsUnder(path, prefix))
            {
                continue;
            }

            if (prefix.Length > bestLength
                || (prefix.Length == bestLength
                    && zone.Verdict.ConservativenessRank() > bestZone!.Verdict.ConservativenessRank()))
            {
                bestZone = zone;
                bestLength = prefix.Length;
            }
        }

        if (bestZone is not null)
        {
            return new Classification(bestZone.Verdict, bestZone.Id, ClaimedStage);
        }

        var classification = classifier.Classify(segments);

        // App-inventory zones act ONLY over a REVIEW verdict (increments 3 & 4),
        // so an already-classified keep (capture / carve_out / secret) or ignore is
        // never overridden — the forget-nothing ruleset stages always win.
        if (classification.Verdict != Verdict.Review)
        {
            return classification;
        }

        // Category modules — explicit, user-configured decisions about a whole
        // category (games, media…). Like the app-inventory zones they act ONLY over a
        // REVIEW verdict, so a keep/secret is never touched. Every KEEP is applied
        // before every IGNORE (keeping data always beats dropping a re-acquirable
        // bulk), and a module decision beats the inventory heuristics below it (it is
        // an explicit user choice, they are guesses).
        if (TryModuleKeep(segments, categoryModules, out var moduleKeepId))
        {
            return new Classification(Verdict.CaptureUser, moduleKeepId, ModuleStage);
        }

        // Increment 4 — app data zones (%AppData% kept, %LocalAppData% surfaced):
        // checked before the ignores so keeping data beats ignoring a re-acquirable
        // install when the two ever overlap. Additive-only (review/capture), so it
        // can only make a REVIEW item more conservative.
        if (TryAppDataKeep(path, appDataZones, out var appDataId, out var appDataVerdict))
        {
            return new Classification(appDataVerdict, appDataId, AppDataStage);
        }

        // Category module IGNORE: drop the re-acquirable bulk of a user-chosen
        // category, but never a save-named subtree (forget-nothing, save_carve_backs).
        if (TryModuleIgnore(segments, categoryModules, out var moduleIgnoreId))
        {
            return new Classification(Verdict.Ignore, moduleIgnoreId, ModuleStage);
        }

        // Increment 3 — reinstall zones: a folder the inventory identified as a
        // re-acquirable app install. Downgrade REVIEW → IGNORE for its
        // re-downloadable content only, never a data-named subtree.
        if (TryReinstallIgnore(path, segments, reinstallZones, out var reinstallId))
        {
            return new Classification(Verdict.Ignore, reinstallId, ReinstallStage);
        }

        return classification;
    }

    /// <summary>
    /// A REVIEW item belongs to a KEEP category when any active KEEP module matches
    /// it (by folder name or file extension). First match wins for attribution.
    /// </summary>
    private static bool TryModuleKeep(
        string[] segments, IReadOnlyList<CategoryModule> modules, out string moduleId)
    {
        moduleId = string.Empty;
        foreach (var module in modules)
        {
            if (module.Action == ModuleAction.Keep && ModuleMatches(segments, module, out _))
            {
                moduleId = module.Id;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A REVIEW item is droppable when an active IGNORE module matches it AND — when
    /// the module carves back saves — no user-data-named segment appears in its path
    /// (the app-zones net, reused via <see cref="AppDataFolders"/>): the user drops
    /// the game bulk, never the saves inside it. For a folder match the net starts
    /// BELOW the matched folder; for an extension match there is no category folder
    /// (folderMatchDepth == -1, so the net starts at 0 and scans the whole path) — a
    /// media file sitting under a save*/config*/… folder must be spared just the same.
    /// First match wins.
    /// </summary>
    private static bool TryModuleIgnore(
        string[] segments, IReadOnlyList<CategoryModule> modules, out string moduleId)
    {
        moduleId = string.Empty;
        foreach (var module in modules)
        {
            if (module.Action != ModuleAction.Ignore || !ModuleMatches(segments, module, out var folderMatchDepth))
            {
                continue;
            }

            // folderMatchDepth is -1 for an extension match, so +1 == 0 scans the
            // whole path — the save-name net must never be silently skipped.
            if (module.SaveCarveBacks && HasDataNamedSegmentBelow(segments, folderMatchDepth + 1))
            {
                continue;
            }

            moduleId = module.Id;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Does a category module match this path? By folder name (a path segment equal
    /// to one of the module's tokens — <paramref name="folderMatchDepth"/> is the
    /// shallowest such segment, defining the category root for the save-name net) or,
    /// failing that, by the leaf's file extension (no folder context: depth stays -1).
    /// </summary>
    private static bool ModuleMatches(string[] segments, CategoryModule module, out int folderMatchDepth)
    {
        folderMatchDepth = -1;
        for (var i = 0; i < segments.Length; i++)
        {
            foreach (var name in module.FolderNames)
            {
                if (GlobPattern.MatchesName(name, segments[i]))
                {
                    folderMatchDepth = i;
                    return true;
                }
            }
        }

        if (module.Extensions.Count > 0 && segments.Length > 0)
        {
            var leaf = segments[^1];
            var dot = leaf.LastIndexOf('.');
            if (dot >= 0 && dot < leaf.Length - 1)
            {
                var extension = leaf[(dot + 1)..];
                foreach (var candidate in module.Extensions)
                {
                    if (string.Equals(candidate, extension, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// A REVIEW item belongs to an app's data folder when it sits under an
    /// inventory-derived %AppData%/%LocalAppData% zone. Longest matching prefix
    /// wins; on a tie the most conservative verdict wins (capture over review).
    /// </summary>
    private static bool TryAppDataKeep(
        string path,
        IReadOnlyList<(AppDataZone Zone, string Prefix)> appDataZones,
        out string zoneId,
        out Verdict verdict)
    {
        zoneId = string.Empty;
        verdict = Verdict.Review;
        var bestLength = -1;
        foreach (var (zone, prefix) in appDataZones)
        {
            if (!IsUnder(path, prefix))
            {
                continue;
            }

            if (prefix.Length > bestLength
                || (prefix.Length == bestLength && zone.Verdict.ConservativenessRank() > verdict.ConservativenessRank()))
            {
                bestLength = prefix.Length;
                zoneId = zone.Id;
                verdict = zone.Verdict;
            }
        }

        return bestLength >= 0;
    }

    /// <summary>
    /// A REVIEW item is re-installable when it sits under an inventory install dir
    /// AND no path segment below that dir is a user-data name (the app-zones
    /// carve-backs, reused via <see cref="AppDataFolders"/>). Longest matching
    /// prefix wins for report attribution.
    /// </summary>
    private static bool TryReinstallIgnore(
        string path,
        string[] segments,
        IReadOnlyList<(ReinstallZone Zone, string Prefix)> reinstallZones,
        out string zoneId)
    {
        zoneId = string.Empty;
        var bestLength = -1;
        foreach (var (zone, prefix) in reinstallZones)
        {
            if (prefix.Length <= bestLength || !IsUnder(path, prefix))
            {
                continue;
            }

            if (HasDataNamedSegmentBelow(segments, prefix))
            {
                continue;
            }

            bestLength = prefix.Length;
            zoneId = zone.Id;
        }

        return bestLength >= 0;
    }

    private static bool HasDataNamedSegmentBelow(string[] segments, string prefix) =>
        HasDataNamedSegmentBelow(segments, ScanPaths.Split(prefix).Length);

    private static bool HasDataNamedSegmentBelow(string[] segments, int startDepth)
    {
        for (var depth = startDepth; depth < segments.Length; depth++)
        {
            if (AppDataFolders.IsDataName(segments[depth]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsUnderAny(string path, IReadOnlyList<string> prefixes)
    {
        foreach (var prefix in prefixes)
        {
            if (IsUnder(path, prefix))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsUnder(string path, string prefix) =>
        path.Length >= prefix.Length
        && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        && (path.Length == prefix.Length || path[prefix.Length] == '/');
}
