using System.IO;

namespace ReDows.Gui.Reviewing;

/// <summary>
/// Reads a folder from the real disk, READ-ONLY: immediate children with sizes (folders are sized
/// recursively). Skips inaccessible entries and does not follow reparse points (junctions/symlinks —
/// no cycles, no double counting), mirroring the scan engine's policy. Runs off the UI thread and
/// honors cancellation, so opening a very large folder never freezes the window.
/// </summary>
public sealed class WindowsFolderBrowser : IFolderBrowser
{
    public Task<IReadOnlyList<EntryRow>> ListAsync(string directoryPath, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<EntryRow>>(() => List(directoryPath, cancellationToken), cancellationToken);

    private static IReadOnlyList<EntryRow> List(string directoryPath, CancellationToken cancellationToken)
    {
        var listing = new EnumerationOptions { IgnoreInaccessible = true };
        var info = new DirectoryInfo(directoryPath);
        var entries = new List<EntryRow>();

        foreach (var directory in info.EnumerateDirectories("*", listing))
        {
            cancellationToken.ThrowIfCancellationRequested();
            entries.Add(new EntryRow(directory.Name, directory.FullName, true, FolderSize(directory, cancellationToken)));
        }

        foreach (var file in info.EnumerateFiles("*", listing))
        {
            cancellationToken.ThrowIfCancellationRequested();
            entries.Add(new EntryRow(file.Name, file.FullName, false, SafeLength(file)));
        }

        return entries;
    }

    private static long FolderSize(DirectoryInfo directory, CancellationToken cancellationToken)
    {
        var recursive = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint, // never follow junctions/symlinks (cycles)
        };

        long total = 0;
        foreach (var file in directory.EnumerateFiles("*", recursive))
        {
            cancellationToken.ThrowIfCancellationRequested();
            total += SafeLength(file);
        }

        return total;
    }

    private static long SafeLength(FileInfo file)
    {
        try
        {
            return file.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
