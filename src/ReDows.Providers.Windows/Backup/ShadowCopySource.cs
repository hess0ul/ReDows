using ReDows.Core.Backup;

namespace ReDows.Providers.Windows.Backup;

/// <summary>
/// A read-only copy source that rescues locked files. It reads each file live first; if the
/// live read fails because the file is in use (a sharing violation) or access is denied, it
/// falls back to reading the same file from a frozen volume shadow copy, which has no lock.
/// A shadow is created on demand only when a lock is actually hit (see <see cref="IVolumeSnapshotSet"/>),
/// so an idle machine pays nothing. When no shadow is available the original failure stands —
/// it is reported by the copy engine, never silently skipped (invariant #2). READ-ONLY
/// throughout: neither the live file nor the shadow is ever opened for write (invariant #3).
/// </summary>
public sealed class ShadowCopySource(ICopySource live, IVolumeSnapshotSet snapshots) : ICopySource, IDisposable
{
    private readonly HashSet<string> _rescued = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The paths that could only be read via a shadow copy (a live lock was bypassed).</summary>
    public IReadOnlyCollection<string> RescuedPaths => _rescued;

    public Stream OpenRead(string path)
    {
        try
        {
            return live.OpenRead(path);
        }
        catch (Exception ex) when (IsLockOrAccess(ex))
        {
            var windowsPath = path.Replace('/', '\\');
            var deviceRoot = snapshots.DeviceRootForVolumeOf(windowsPath);
            if (deviceRoot is null)
            {
                throw; // no shadow available → the original failure is reported as-is.
            }

            var stream = OpenShadow(ShadowPath.Rewrite(deviceRoot, windowsPath));
            _rescued.Add(path);
            return stream;
        }
    }

    /// <summary>A lock (sharing violation) or denied access is rescuable; a genuinely missing file is not.</summary>
    private static bool IsLockOrAccess(Exception ex) =>
        ex is UnauthorizedAccessException ||
        (ex is IOException and not FileNotFoundException and not DirectoryNotFoundException);

    private static Stream OpenShadow(string shadowPath) =>
        new FileStream(
            shadowPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1 << 16,
            FileOptions.SequentialScan);

    public void Dispose() => snapshots.Dispose();
}
