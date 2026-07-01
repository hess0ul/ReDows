namespace ReDows.Providers.Windows.Backup;

/// <summary>
/// A set of volume shadow copies — frozen, point-in-time "photos" of a volume. Reading a
/// file from its volume's shadow bypasses a live lock, because the shadow is a read-only
/// snapshot taken at one instant. Photos are created on demand (one per volume) and released
/// when the set is disposed. READ-ONLY by nature: a shadow is never written to, so the
/// scanned source is never modified (invariant #3).
/// </summary>
public interface IVolumeSnapshotSet : IDisposable
{
    /// <summary>
    /// The device root of a shadow for the volume that <paramref name="windowsPath"/> lives on,
    /// creating the shadow on first use for that volume and reusing it afterwards. Returns
    /// <c>null</c> when a shadow cannot be made (e.g. not elevated, or a network path) — the
    /// caller then reports the file as a failure rather than skipping it silently.
    /// </summary>
    string? DeviceRootForVolumeOf(string windowsPath);
}

/// <summary>
/// Maps a real file path to the path of the same file inside a shadow copy: the volume root
/// is swapped for the shadow's device root. For example, with a device root of
/// <c>\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy3</c>, the file <c>C:\Users\x\f</c>
/// becomes <c>\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy3\Users\x\f</c>.
/// </summary>
public static class ShadowPath
{
    public static string Rewrite(string deviceRoot, string fullWindowsPath)
    {
        var root = Path.GetPathRoot(fullWindowsPath);
        if (string.IsNullOrEmpty(root))
        {
            throw new ArgumentException($"a rooted path is required, got '{fullWindowsPath}'", nameof(fullWindowsPath));
        }

        var remainder = fullWindowsPath[root.Length..].TrimStart('\\');
        return deviceRoot.TrimEnd('\\') + "\\" + remainder;
    }
}
