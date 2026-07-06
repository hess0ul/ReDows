namespace ReDows.Core.Saves;

/// <summary>Where a loaded ludusavi manifest came from, so the UI can say what happened.</summary>
public enum LudusaviSourceStatus
{
    /// <summary>Freshly fetched from the network.</summary>
    Downloaded,

    /// <summary>Read from a previous download already on this machine.</summary>
    Cached,

    /// <summary>No manifest available (offline and never cached). The result's manifest is empty.</summary>
    Failed,
}

/// <summary>
/// The outcome of loading the ludusavi manifest: the parsed <see cref="Manifest"/>, where it came from,
/// and an optional human note (e.g. why a refresh fell back to the cached copy).
/// </summary>
public sealed record LudusaviLoadResult(LudusaviManifest Manifest, LudusaviSourceStatus Status, string? Detail = null);

/// <summary>
/// Source of the ludusavi manifest: the optional per-game save catalogue. A seam: the real
/// implementation downloads it onto THIS machine and caches it (its data is CC BY-NC-SA, so it is never
/// bundled with ReDows); a test swaps a fake. Loading never throws for a network problem. It falls back
/// to a cached copy, or an empty manifest, so a scan is never blocked by being offline. Only an explicit
/// user cancellation propagates.
/// </summary>
public interface ILudusaviSource
{
    Task<LudusaviLoadResult> LoadAsync(bool forceRefresh, CancellationToken cancellationToken);
}
