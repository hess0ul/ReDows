using System.IO;

namespace ReDows.Gui.Reviewing;

/// <summary>One row in the review explorer: a file or a folder, with its (recursive, for folders) size.</summary>
public sealed record EntryRow(string Name, string FullPath, bool IsDirectory, long Bytes)
{
    public string SizeText => Format.Bytes(Bytes);

    public string Kind
    {
        get
        {
            if (IsDirectory)
            {
                return "Folder";
            }

            // Path.GetExtension treats a leading-dot name (".gitignore", ".lmstudio-home-pointer") as
            // one giant "extension". A dotfile has no real type, and a genuine extension is a short
            // suffix — so a dotfile, or an absurdly long pseudo-extension, is just a file.
            var extension = Path.GetExtension(Name).TrimStart('.');
            return !Name.StartsWith('.') && extension.Length is > 0 and <= 8
                ? extension.ToUpperInvariant()
                : "File";
        }
    }

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
