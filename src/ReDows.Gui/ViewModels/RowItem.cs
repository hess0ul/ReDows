using System.IO;

namespace ReDows.Gui.ViewModels;

/// <summary>One line in the trash panel: a dropped file/folder, restorable. (File reused from the
/// earlier RowItem; the review list itself now binds plain entries, no per-row keep state.)</summary>
public sealed record TrashRow(string Name, string SizeText, string FullPath)
{
    public static TrashRow From(string fullPath, long bytes)
    {
        var name = Path.GetFileName(fullPath.TrimEnd('\\'));
        return new TrashRow(name.Length == 0 ? fullPath : name, Format.Bytes(bytes), fullPath);
    }
}
