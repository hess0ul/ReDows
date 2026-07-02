namespace ReDows.Gui.Session;

/// <summary>One review head-directory remembered from the last scan (mirrors a Scan result's review row).</summary>
public sealed record SessionReviewRoot(string Size, string Items, string Folder, long Bytes);

/// <summary>One item the user dropped in the review trash — a decision to remember (path + its size).</summary>
public sealed record SessionTrashItem(string Path, long Bytes);

/// <summary>
/// A persisted ReDows session: the last scan (its summary + the manifest to back up) AND the user's
/// decisions (what they trashed in the review sorter, and which apps they unticked for reinstall).
/// Written after a scan and whenever a decision changes, so re-opening ReDows can offer to resume where
/// the user left off instead of starting over. The manifest and settings live in their own files; this
/// only records the summary + decisions. <see cref="DeselectedApps"/> is a deny-list of app keys the
/// user unticked (default is "reinstall everything") — null/absent in older sessions, read as empty.
/// </summary>
public sealed record SessionFile(
    string ScannedUtc,
    string? Root,
    string ManifestPath,
    string KeepText,
    string ReviewText,
    string IgnoreText,
    IReadOnlyList<SessionReviewRoot> ReviewRoots,
    IReadOnlyList<SessionTrashItem> Trash,
    IReadOnlyList<string>? DeselectedApps = null);

/// <summary>
/// Reads and writes the ReDows session on disk. A seam: the real store is a JSON file under the user's
/// local app data; a test swaps a fake or an in-memory one. Load returns null when there is no (readable)
/// session yet.
/// </summary>
public interface ISessionStore
{
    SessionFile? Load();

    void Save(SessionFile session);

    /// <summary>Forget the saved session ("start fresh").</summary>
    void Clear();
}
