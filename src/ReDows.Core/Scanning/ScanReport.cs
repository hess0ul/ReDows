using ReDows.Core.Classification;
using ReDows.Core.Rules;

namespace ReDows.Core.Scanning;

/// <summary>
/// Rule ids reserved for the engine itself (rules/README.md): situations decided
/// by code, not by the ruleset, but still part of the total accounting — every
/// one of them is a counted, visible verdict, never a silent skip.
/// </summary>
public static class EngineRuleIds
{
    public const string Stage = "engine";

    /// <summary>A directory that could not be enumerated: one "unknown subtree" item, verdict REVIEW.</summary>
    public const string Inaccessible = "engine.inaccessible";

    /// <summary>A symlink/junction/alias that is not traversed (cycles, double counting), verdict NOTE_ONLY.</summary>
    public const string ReparsePoint = "engine.reparse_point";

    /// <summary>
    /// A non-surrogate reparse DIRECTORY that is not traversed (ProjFS/WCI root,
    /// unknown or unreadable tag): unlike a link, its contents are unique and
    /// were never walked — a blind spot, verdict REVIEW (forget-nothing).
    /// </summary>
    public const string UnknownReparse = "engine.unknown_reparse";

    /// <summary>Anything under a profile directory that belongs to no ProfileList entry, verdict REVIEW.</summary>
    public const string OrphanProfile = "engine.orphan_profile";

    /// <summary>ReDows' own output, excluded from its own scan, verdict IGNORE.</summary>
    public const string SelfOutput = "engine.self_output";
}

/// <summary>
/// A dynamic zone claimed at scan time by an index parser (INDEX_EXTERNE §D-15):
/// a config file pointed at this subtree (relocated mail store, Calibre library,
/// VM folder…). Safety direction is structural: a claimed zone may only ADD
/// review/capture — never ignore — so a wrong index can over-capture (benign)
/// but never lose data. Evaluated before the ruleset stages (normative order:
/// errors → claimed → carve_out → deny → capture → default).
/// </summary>
public sealed record ClaimedZone
{
    public ClaimedZone(string id, string pathPrefix, Verdict verdict, string? note = null)
    {
        if (verdict is Verdict.Ignore or Verdict.NoteOnly)
        {
            throw new ArgumentException(
                $"a claimed zone may only review or capture, never '{verdict.Format()}' (safety direction)", nameof(verdict));
        }

        if (string.IsNullOrEmpty(id) || !id.Contains(':'))
        {
            throw new ArgumentException(
                "claimed zone ids must carry a ':' namespace (e.g. 'index:app:target@user') — ':' cannot appear " +
                "in rule or engine ids, so report attribution can never collide", nameof(id));
        }

        Id = id;
        PathPrefix = pathPrefix;
        Verdict = verdict;
        Note = note;
    }

    public string Id { get; }

    public string PathPrefix { get; }

    public Verdict Verdict { get; }

    public string? Note { get; }
}

/// <summary>
/// A dynamic zone fed at scan time from the application inventory: a directory the
/// inventory identified as an installed app's location (ARP InstallLocation, a game
/// library, a package-manager folder). The app is re-acquirable, so its
/// re-downloadable content is IGNORE — but ONLY where the ruleset would otherwise
/// REVIEW it (never over a keep: a carve-out / capture survives), and never inside a
/// data-named subtree (config/save/userdata… stay REVIEW, see <see cref="AppDataFolders"/>).
/// Evaluated AFTER the ruleset, so the forget-nothing stages always win — this is the
/// one engine-fed zone that may ignore, and it is deliberately the last word.
/// </summary>
public sealed record ReinstallZone
{
    public ReinstallZone(string id, string pathPrefix)
    {
        if (string.IsNullOrEmpty(id) || !id.Contains(':'))
        {
            throw new ArgumentException(
                "reinstall zone ids must carry a ':' namespace (e.g. 'app:arp:Notepad++') — ':' cannot appear " +
                "in rule or engine ids, so report attribution can never collide", nameof(id));
        }

        Id = id;
        PathPrefix = pathPrefix;
    }

    public string Id { get; }

    public string PathPrefix { get; }
}

/// <summary>Items classified by a rule carrying the dpapi_machine_bound flag: export/sync BEFORE the reset.</summary>
public sealed record PreResetAlert(string RuleId, long Items);

/// <summary>
/// Scan parameters. <paramref name="Roots"/> walks the given roots instead of the
/// context's volume roots (subtree scan); <paramref name="ExcludedOutputPaths"/>
/// lists paths produced by ReDows itself (report file…), classified engine.self_output;
/// <paramref name="ClaimedZones"/> are index-derived dynamic zones.
/// </summary>
/// <param name="OnCapture">
/// Called once for every CAPTURE-verdict item, in scan order, before the report is
/// built — the per-item manifest sink. Invoked in lockstep with the CAPTURE verdict
/// tally, so a sink that writes one line per call emits exactly as many lines as the
/// report counts CAPTURE items (the manifest's self-consistency guarantee).
/// </param>
public sealed record ScanOptions(
    IReadOnlyList<string>? Roots = null,
    IReadOnlyList<string>? ExcludedOutputPaths = null,
    Action<long, string>? OnProgress = null,
    long ProgressInterval = 50_000,
    IReadOnlyList<ClaimedZone>? ClaimedZones = null,
    Action<ManifestEntry>? OnCapture = null,
    IReadOnlyList<ReinstallZone>? ReinstallZones = null);

public sealed record VerdictTotals(long Items, long Bytes);

public sealed record RuleHit(string RuleId, string Stage, Verdict Verdict, long Items, long Bytes);

/// <summary>REVIEW items aggregated under a head directory (top of the human work queue).</summary>
public sealed record ReviewBucket(string Directory, long Items, long Bytes);

/// <summary>
/// The complete result of a scan. Total-accounting invariant, verifiable from the
/// data itself: every walked item received exactly one verdict, so
/// <see cref="UnaccountedItems"/> must be 0 — the report displays that equation
/// instead of asking to be trusted.
/// </summary>
public sealed record ScanReport(
    bool Partial,
    IReadOnlyList<string> Roots,
    long TotalItems,
    long TotalFiles,
    long TotalDirectories,
    long TotalUnknownSubtrees,
    long TotalBytes,
    IReadOnlyDictionary<Verdict, VerdictTotals> ByVerdict,
    IReadOnlyList<RuleHit> RuleHits,
    IReadOnlyList<ReviewBucket> ReviewRollup,
    IReadOnlyList<UninstantiatedRule> UninstantiatedRules,
    IReadOnlyList<PreResetAlert> PreResetAlerts,
    IReadOnlyList<string> DeclaredLimits)
{
    public long AccountedItems => ByVerdict.Values.Sum(v => v.Items);

    /// <summary>Must be 0: IGNORE ∪ NOTE_ONLY ∪ REVIEW ∪ CAPTURE = everything walked.</summary>
    public long UnaccountedItems => TotalItems - AccountedItems;

    /// <summary>
    /// V1 limits, stated rather than hidden (deny-list §0-5: a known deviation is
    /// declared, never silent).
    /// </summary>
    public static readonly IReadOnlyList<string> V1Limits =
    [
        "Hard links are counted once per path: a file with N names contributes its size N times.",
        "Bytes are logical file sizes, not disk usage (NTFS compression, sparse ranges and alternate data streams are not measured).",
        "A directory that could not be enumerated is one 'unknown subtree' item: its contents are absent from every figure. 'Zero gaps' therefore means zero KNOWN gaps.",
        "Cloud placeholders (OneDrive Files-On-Demand…) are counted at their logical size; no content is downloaded.",
        "A reparse-point directory with an unknown or unreadable tag is not traversed: it is one counted REVIEW item (engine.unknown_reparse) and its contents are absent from every figure.",
        "Dynamic deny-list sources (FilesNotToBackup…) are a later block.",
        "The application inventory feeds re-installable install-dir zones: their re-downloadable content is IGNORED where the ruleset would only REVIEW, never over a keep and never a data-named subtree — enabled unless --no-reinstall.",
    ];
}
