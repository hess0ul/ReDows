using ReDows.Core.Backup;

namespace ReDows.Providers.Windows.Backup;

/// <summary>
/// Real-machine read-only copy source. Opens each file for READ only, sharing with other
/// readers/writers so a file being used elsewhere can still be read where possible — it
/// never opens for write, so the scanned source is never modified (invariant #3).
/// </summary>
public sealed class FileSystemCopySource : ICopySource
{
    public Stream OpenRead(string path) =>
        new FileStream(
            ToWindowsPath(path),
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1 << 16,
            FileOptions.SequentialScan);

    internal static string ToWindowsPath(string normalized) => normalized.Replace('/', '\\');
}

/// <summary>
/// Writes the backup under a destination directory (local disk, USB key, or UNC network
/// share — all just paths). Relative paths use '/'; they are joined onto the destination
/// with the OS separator. Creates parent directories as needed.
/// </summary>
public sealed class FileSystemSink(string destinationRoot) : IBackupSink
{
    private readonly string _root = Path.GetFullPath(destinationRoot);

    public Stream OpenWrite(string relativePath)
    {
        var full = Resolve(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        return new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1 << 16);
    }

    public Stream OpenReadBack(string relativePath) =>
        new FileStream(Resolve(relativePath), FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 16);

    private string Resolve(string relativePath)
    {
        // Defence: a relative path must stay strictly UNDER the destination root. '.'/'..'
        // segments are dropped, then the resolved path is checked against the root WITH a
        // trailing separator so a sibling like 'dest-x' can never satisfy a prefix of 'dest'.
        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(s => s is not "." and not "..")
            .ToArray();
        var full = Path.GetFullPath(Path.Combine(new[] { _root }.Concat(segments).ToArray()));
        var rootWithSeparator = _root.EndsWith(Path.DirectorySeparatorChar)
            ? _root
            : _root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"refusing to write outside the destination: '{relativePath}'");
        }

        return full;
    }
}
