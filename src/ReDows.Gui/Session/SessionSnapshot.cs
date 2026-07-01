using ReDows.Gui.Scanning;

namespace ReDows.Gui.Session;

/// <summary>
/// Pure conversions between a live scan result + the review trash and the persisted <see cref="SessionFile"/>
/// — so the "save my session" and "resume from my session" mapping is unit-tested without any I/O.
/// </summary>
public static class SessionSnapshot
{
    /// <summary>Capture the scan summary + the user's trash decisions into a session to persist.</summary>
    public static SessionFile Build(ScanResultView result, IReadOnlyDictionary<string, long> trash, string? root, string scannedUtc) =>
        new(
            scannedUtc,
            root,
            result.ManifestPath ?? "",
            result.KeepText,
            result.ReviewText,
            result.IgnoreText,
            result.TopReview.Select(row => new SessionReviewRoot(row.Size, row.Items, row.Folder, row.Bytes)).ToList(),
            trash.Select(entry => new SessionTrashItem(entry.Key, entry.Value)).ToList());

    /// <summary>Rebuild a Scan result view from a session, so the Review and Backup screens work on resume.</summary>
    public static ScanResultView ToResultView(SessionFile session) =>
        new(
            Partial: false,
            KeepText: session.KeepText,
            ReviewText: session.ReviewText,
            IgnoreText: session.IgnoreText,
            NoteText: "0 items",
            Balanced: true,
            EquationText: "Restored from your last session.",
            Alerts: [],
            TopReview: session.ReviewRoots.Select(root => new ReviewFolderRow(root.Size, root.Items, root.Folder, root.Bytes)).ToList(),
            Duplicates: null,
            InstalledApps: null,
            ManifestPath: session.ManifestPath);
}
