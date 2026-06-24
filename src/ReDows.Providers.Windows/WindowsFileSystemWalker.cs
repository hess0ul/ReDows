using System.IO.Enumeration;
using ReDows.Core.Scanning;
using ReDows.Providers.Windows.Native;

namespace ReDows.Providers.Windows;

/// <summary>
/// Real-machine IFileSystemWalker. Recursion is driven here, not by .NET
/// (RecurseSubdirectories=false), for two reasons: error attribution — the
/// framework cannot tell us WHICH queued directory failed to open, we can — and
/// the shared reparse policy (never follow symlinks/junctions, do descend into
/// cloud placeholder directories). The three default-.NET traps are neutralized:
/// AttributesToSkip=0 (hidden/system files must be seen), IgnoreInaccessible=false
/// (every refusal becomes a counted 'unknown subtree' entry), and no framework
/// recursion (which would follow junctions into cycles).
/// </summary>
public sealed class WindowsFileSystemWalker : IFileSystemWalker
{
    private static readonly EnumerationOptions SingleDirectory = new()
    {
        RecurseSubdirectories = false,
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        ReturnSpecialDirectories = false,
    };

    public IEnumerable<ScanEntry> Walk(string rootPath)
    {
        rootPath = ScanPaths.AnchorDriveRoot(rootPath);

        var pending = new Stack<string>();
        pending.Push(rootPath);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in EnumerateOneDirectory(directory, pending))
            {
                yield return entry;
            }
        }
    }

    private static IEnumerable<ScanEntry> EnumerateOneDirectory(string directory, Stack<string> pending)
    {
        // Enumerate through the extended-length form: Win32 normalization strips
        // trailing dots/spaces from components, so re-opening a "trail." entry
        // by its logical path would land on "trail" — its real children silently
        // skipped with NO marker (a gap the equation cannot see) and the wrong
        // tree double-walked. Entry paths stay LOGICAL (rebuilt from the parent):
        // the prefix must never leak into classification or the report.
        var extended = directory.StartsWith(@"\\?\", StringComparison.Ordinal)
            ? directory
            : @"\\?\" + directory;

        IEnumerator<ScanEntry>? enumerator;
        Exception? failure = null;
        try
        {
            enumerator = new FileSystemEnumerable<ScanEntry>(
                extended,
                (ref FileSystemEntry entry) => new ScanEntry(
                    Path.Join(directory.AsSpan(), entry.FileName),
                    entry.IsDirectory,
                    entry.IsDirectory ? 0 : entry.Length,
                    (int)entry.Attributes),
                SingleDirectory).GetEnumerator();
        }
        catch (Exception ex) when (IsEnumerationFailure(ex))
        {
            enumerator = null;
            failure = ex;
        }

        if (enumerator is not null)
        {
            using (enumerator)
            {
                while (true)
                {
                    ScanEntry? entry = null;
                    try
                    {
                        if (enumerator.MoveNext())
                        {
                            entry = enumerator.Current;
                        }
                    }
                    catch (Exception ex) when (IsEnumerationFailure(ex))
                    {
                        // The handle is dead: entries already yielded stay valid, and
                        // the marker says the rest of this directory is unknown.
                        failure = ex;
                    }

                    if (entry is null)
                    {
                        break;
                    }

                    if ((entry.Attributes & ReparsePoints.ReparsePointAttribute) != 0)
                    {
                        entry = entry with { ReparseTag = GetReparseTag(entry.Path) };
                    }

                    if (entry.IsDirectory && ReparsePoints.ShouldTraverse(entry.ReparseTag))
                    {
                        pending.Push(entry.Path);
                    }

                    yield return entry;
                }
            }
        }

        if (failure is not null)
        {
            yield return UnknownSubtree(directory, failure);
        }
    }

    private static ScanEntry UnknownSubtree(string directory, Exception ex) => new(
        directory,
        IsDirectory: true,
        SizeBytes: 0,
        Attributes: 0,
        Error: $"{ex.GetType().Name}: {ex.Message}");

    private static bool IsEnumerationFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or System.Security.SecurityException;

    /// <summary>Reads the reparse tag of one entry (metadata only, nothing opened for data access).</summary>
    private static uint GetReparseTag(string path)
    {
        var findPath = path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path : @"\\?\" + path;
        var handle = NativeMethods.FindFirstFileExW(
            findPath,
            NativeMethods.FindExInfoBasic,
            out var data,
            NativeMethods.FindExSearchNameMatch,
            IntPtr.Zero,
            0);

        if (handle == NativeMethods.InvalidHandleValue)
        {
            // The attribute says reparse but the tag is unreadable (race, filter
            // driver): fail closed — never traverse an unverified link.
            return ReparsePoints.UnreadableTag;
        }

        NativeMethods.FindClose(handle);
        return (data.dwFileAttributes & ReparsePoints.ReparsePointAttribute) != 0 ? data.dwReserved0 : 0;
    }
}

/// <summary>
/// Real-machine IFileSystemView for context conditions (ancestor markers).
/// A probe failure yields no names: the condition then simply does not prove its
/// marker, and the collision-prone rule conservatively stays unapplied (REVIEW).
/// </summary>
public sealed class WindowsFileSystemView : IFileSystemView
{
    private static readonly EnumerationOptions ProbeOptions = new()
    {
        RecurseSubdirectories = false,
        AttributesToSkip = 0,
        IgnoreInaccessible = true,
        ReturnSpecialDirectories = false,
    };

    public IEnumerable<string> EnumerateEntryNames(string directoryPath)
    {
        try
        {
            return new FileSystemEnumerable<string>(
                // Conditions probe ancestors down to the bare drive segment
                // ("C:") — unanchored, that is the process CWD, and a project
                // marker in the launch directory would enable collision-prone
                // ignores volume-wide (§0-8 regression).
                ScanPaths.AnchorDriveRoot(directoryPath),
                (ref FileSystemEntry entry) => entry.FileName.ToString(),
                ProbeOptions).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            return [];
        }
    }
}
