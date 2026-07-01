using System.IO;

namespace ReDows.Gui.Reviewing;

/// <summary>One row in the review explorer: a file or a folder, with its (recursive, for folders) size.</summary>
public sealed record EntryRow(string Name, string FullPath, bool IsDirectory, long Bytes)
{
    public string SizeText => Format.Bytes(Bytes);

    public string Kind => IsDirectory
        ? "Folder"
        : Path.GetExtension(Name) is { Length: > 1 } extension ? extension.TrimStart('.').ToUpperInvariant() : "File";

    public string Icon => IsDirectory ? "📁" : "📄";
}

/// <summary>
/// Lists a folder's immediate children with sizes — READ-ONLY, on demand (the app never keeps the
/// whole tree in memory; it re-reads a folder when the user opens it). A seam so the review
/// view-model is testable off a fake, without touching the disk.
/// </summary>
public interface IFolderBrowser
{
    Task<IReadOnlyList<EntryRow>> ListAsync(string directoryPath, CancellationToken cancellationToken);
}
