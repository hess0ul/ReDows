namespace ReDows.Core.Scanning;

/// <summary>
/// One file-system object seen by the walker, or a synthetic "unknown subtree"
/// marker when a directory could not be enumerated (<see cref="Error"/> is then
/// non-null and the directory's contents are absent from the stream — counted,
/// never silently skipped).
/// </summary>
public sealed record ScanEntry(
    string Path,
    bool IsDirectory,
    long SizeBytes,
    int Attributes,
    uint ReparseTag = 0,
    string? Error = null);

/// <summary>
/// Streaming view of a file-system tree. The engine consumes only this stream:
/// the real Windows walker and the test fake are interchangeable. Contract:
/// every object under the root is yielded exactly once; a directory that cannot
/// be enumerated yields one error entry instead of being skipped; reparse points
/// are yielded but only traversed when <see cref="ReparsePoints.ShouldTraverse"/>
/// allows it (the walker and the engine must share that policy).
/// </summary>
public interface IFileSystemWalker
{
    IEnumerable<ScanEntry> Walk(string rootPath);
}
