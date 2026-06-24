using ReDows.Core.Classification;
using ReDows.Core.Rules;

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

                var classification = Classify(classifier, entry, path, segments, excludedOutputs, orphanRoots, claimedZones);
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
        IReadOnlyList<(ClaimedZone Zone, string Prefix)> claimedZones)
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

        return classifier.Classify(segments);
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
