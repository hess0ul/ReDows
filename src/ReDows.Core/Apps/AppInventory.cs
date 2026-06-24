namespace ReDows.Core.Apps;

/// <summary>
/// Bucket of an inventory entry. Nothing is dropped (total accounting): noise
/// (components, updates) is classified into visible REVIEW-grade buckets — some
/// real tools ship SystemComponent=1, so the bucket must reach human eyes.
/// </summary>
public enum AppEntryKind
{
    App,
    Component,
    Update,
}

/// <summary>
/// Confidence of a reinstall id. Only exact correlations (winget's own engine,
/// package-manager native ids) may be presented as preferred; a heuristic match
/// is a candidate — a wrong reinstall id is worse than none.
/// </summary>
public enum ReinstallConfidence
{
    Exact,
    Candidate,
}

public sealed record ReinstallHint(string Kind, string Id, ReinstallConfidence Confidence);

/// <summary>
/// One installed-software record from one source. Fields are a strict whitelist:
/// uninstall command lines (may embed tokens) and license/product ids (secrets
/// pipeline) are never copied into the inventory.
/// </summary>
public sealed record AppEntry(
    string Key,
    string Source,
    AppEntryKind Kind,
    string? Name,
    string? Version,
    string? Publisher,
    string? InstallLocation,
    string Scope,
    ReinstallHint? Reinstall = null,
    string? Note = null);

/// <summary>Per-source accounting: enumerated = apps + components + updates + errors.</summary>
public sealed record SourceAccounting(
    string Source,
    long Enumerated,
    long Apps,
    long Components,
    long Updates,
    long Errors)
{
    public long Accounted => Apps + Components + Updates + Errors;

    public long Unaccounted => Enumerated - Accounted;
}

/// <summary>
/// A (source × subject) that could not be read: other users' hives or packages,
/// missing winget… Counted and listed, never guessed (asymmetric policy).
/// </summary>
public sealed record InventoryDegradation(string Source, string Subject, string Reason);

public sealed record AppInventoryReport(
    IReadOnlyList<AppEntry> Entries,
    IReadOnlyList<SourceAccounting> Sources,
    IReadOnlyList<InventoryDegradation> Degradations,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string> DeclaredLimits)
{
    public long TotalUnaccounted => Sources.Sum(s => s.Unaccounted);

    /// <summary>Stated rather than hidden: this inventory is a subset of the full reinstall picture.</summary>
    public static readonly IReadOnlyList<string> V1Limits =
    [
        "Portable applications (no installer) are not detected here: they are files, covered by the file scan and a later block.",
        "Browser extensions, Windows optional features, scheduled tasks/autoruns and third-party drivers are later blocks.",
        "Other users' per-user installs (HKCU hives) and Store packages are invisible to a standard-user scan: counted as degradations, never guessed.",
        "GOG is read from its registry keys (the Galaxy SQLite database is a later step); EA app games are covered through the standard registry only (its own manifest is hardware-encrypted).",
        "A game may legitimately appear twice (launcher source AND registry): records are never destructively merged — linking is a later step.",
        "UniGetUI bundles (.ubundle) have no fixed location: found opportunistically by the file scan, not here.",
        "Reinstall ids are attached only when exact; without --enrich-winget, Win32 apps default to manual reinstall.",
    ];
}
