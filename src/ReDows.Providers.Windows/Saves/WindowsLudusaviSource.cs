using System.IO;
using System.Net.Http;
using ReDows.Core.Saves;

namespace ReDows.Providers.Windows.Saves;

/// <summary>
/// Downloads the ludusavi manifest onto THIS machine and caches it, then parses it. The manifest DATA is
/// compiled from PCGamingWiki (CC BY-NC-SA), so it is never shipped with ReDows. It is fetched, with the
/// user's opt-in, onto their own PC, exactly like ludusavi itself does.
/// <para>Best-effort: a network failure falls back to the cached copy if one exists, else to an empty
/// manifest, never an exception (only a user cancellation propagates). The HTTP handler and the cache
/// path are injectable so the whole thing is unit-tested without a network or the real profile folder.</para>
/// </summary>
public sealed class WindowsLudusaviSource : ILudusaviSource, IDisposable
{
    /// <summary>The community manifest (mtkennerly/ludusavi-manifest, MIT tool; data from PCGamingWiki, CC BY-NC-SA).</summary>
    public const string ManifestUrl = "https://raw.githubusercontent.com/mtkennerly/ludusavi-manifest/master/data/manifest.yaml";

    private readonly HttpClient _http;
    private readonly string _cachePath;
    private readonly string _url;

    public WindowsLudusaviSource(HttpMessageHandler? handler = null, string? cachePath = null, string? url = null)
    {
        // No auto-redirect: the request only ever goes to the URL configured here.
        _http = handler is null ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(90); // a few MB of YAML; generously bounded, the token also cancels
        _cachePath = cachePath ?? DefaultCachePath();
        _url = url ?? ManifestUrl;
    }

    /// <summary>Where the downloaded manifest is cached (next to the session and settings files).</summary>
    public static string DefaultCachePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReDows", "ludusavi-manifest.yaml");

    public async Task<LudusaviLoadResult> LoadAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        // Use the copy already on disk unless the user asked to refresh; no network needed then.
        if (!forceRefresh && TryReadCache() is { } cached)
        {
            return new LudusaviLoadResult(LudusaviManifest.Parse(cached), LudusaviSourceStatus.Cached);
        }

        try
        {
            var yaml = await _http.GetStringAsync(_url, cancellationToken);
            TryWriteCache(yaml);
            return new LudusaviLoadResult(LudusaviManifest.Parse(yaml), LudusaviSourceStatus.Downloaded);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // the user cancelled, so let it propagate, don't dress it up as a failure
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            // Offline or the server hiccuped: fall back to a cached copy, else an empty manifest. Never block a scan.
            return TryReadCache() is { } fallback
                ? new LudusaviLoadResult(LudusaviManifest.Parse(fallback), LudusaviSourceStatus.Cached,
                    $"Could not refresh the catalog ({ex.Message}); using the copy already on this PC.")
                : new LudusaviLoadResult(new LudusaviManifest([]), LudusaviSourceStatus.Failed,
                    $"Could not download the save catalog ({ex.Message}).");
        }
    }

    private string? TryReadCache()
    {
        try
        {
            return File.Exists(_cachePath) ? File.ReadAllText(_cachePath) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void TryWriteCache(string yaml)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            File.WriteAllText(_cachePath, yaml);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The cache is a convenience; a failed write just means the next load downloads again.
        }
    }

    public void Dispose() => _http.Dispose();
}
