using System.Text;
using ReDows.Providers.Windows.Native;

namespace ReDows.Providers.Windows;

/// <summary>
/// One volume as discovered by GUID (never by drive letters: a volume mounted
/// only in a folder, or not mounted at all, must still be accounted for —
/// deny-list §0-3).
/// </summary>
public sealed record DiscoveredVolume(
    string GuidPath,
    IReadOnlyList<string> MountPaths,
    string DriveKind,
    string? Label,
    string? FileSystemFormat,
    long? TotalBytes,
    bool MountPathsUnreadable = false)
{
    /// <summary>Preferred path used to walk and to display: drive letter, else first mount folder.</summary>
    public string? PreferredMountPath =>
        MountPaths.FirstOrDefault(m => m.Length <= 3 && m.Contains(':')) ?? MountPaths.FirstOrDefault();

    public bool Scannable => DriveKind is "fixed" && MountPaths.Count > 0;

    public string? ExclusionReason => Scannable
        ? null
        : MountPathsUnreadable
            ? "mount paths unreadable (volume excluded — the report must never claim 'no mount point' it did not verify)"
            : MountPaths.Count == 0
                ? "no mount point (volume listed for accounting, not walked in v1)"
                : $"drive kind '{DriveKind}' is out of scan scope";
}

/// <summary>Enumerates all volumes via FindFirstVolume/FindNextVolume (read-only).</summary>
public static class VolumeProvider
{
    public static IReadOnlyList<DiscoveredVolume> Discover()
    {
        var volumes = new List<DiscoveredVolume>();
        var buffer = new StringBuilder(260);

        var handle = NativeMethods.FindFirstVolumeW(buffer, buffer.Capacity);
        if (handle == NativeMethods.InvalidHandleValue)
        {
            throw new InvalidOperationException(
                $"FindFirstVolumeW failed (Win32 error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}): no volumes enumerable");
        }

        try
        {
            do
            {
                volumes.Add(Describe(buffer.ToString()));
                buffer.Clear();
            }
            while (NativeMethods.FindNextVolumeW(handle, buffer, buffer.Capacity));
        }
        finally
        {
            NativeMethods.FindVolumeClose(handle);
        }

        return volumes;
    }

    private static DiscoveredVolume Describe(string guidPath)
    {
        var mountPaths = GetMountPaths(guidPath, out var mountPathsUnreadable);
        var driveKind = NativeMethods.GetDriveTypeW(guidPath) switch
        {
            2 => "removable",
            3 => "fixed",
            4 => "remote",
            5 => "cdrom",
            6 => "ramdisk",
            _ => "unknown",
        };

        string? label = null;
        string? fileSystem = null;
        var labelBuffer = new StringBuilder(261);
        var fsBuffer = new StringBuilder(261);
        if (NativeMethods.GetVolumeInformationW(
                guidPath, labelBuffer, labelBuffer.Capacity, out _, out _, out _, fsBuffer, fsBuffer.Capacity))
        {
            label = labelBuffer.Length > 0 ? labelBuffer.ToString() : null;
            fileSystem = fsBuffer.Length > 0 ? fsBuffer.ToString() : null;
        }

        long? totalBytes = null;
        if (NativeMethods.GetDiskFreeSpaceExW(guidPath, out _, out var total, out _))
        {
            totalBytes = (long)total;
        }

        return new DiscoveredVolume(guidPath, mountPaths, driveKind, label, fileSystem, totalBytes, mountPathsUnreadable);
    }

    private static IReadOnlyList<string> GetMountPaths(string guidPath, out bool unreadable)
    {
        const int ErrorMoreData = 234;

        unreadable = false;
        var buffer = new char[1024];
        if (!NativeMethods.GetVolumePathNamesForVolumeNameW(guidPath, buffer, buffer.Length, out var length))
        {
            // A volume mounted in many folders can exceed the buffer: retry with
            // the required size instead of mislabeling it "no mount point".
            if (System.Runtime.InteropServices.Marshal.GetLastWin32Error() == ErrorMoreData && length > buffer.Length)
            {
                buffer = new char[length];
            }

            if (!NativeMethods.GetVolumePathNamesForVolumeNameW(guidPath, buffer, buffer.Length, out length))
            {
                unreadable = true;
                return [];
            }
        }

        // REG_MULTI_SZ-style: NUL-separated, double-NUL-terminated.
        return new string(buffer, 0, Math.Max(0, length))
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(m => m.TrimEnd('\\'))
            .Where(m => m.Length > 0)
            .ToList();
    }
}
