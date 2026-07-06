namespace ReDows.Core.Duplicates;

/// <summary>
/// File-time helper for de-duplication, shared by every caller that hands a <see cref="DuplicateFinder"/>
/// its recency tiebreak, so "the most recent copy is the truth" behaves identically across the CLI and GUI.
/// </summary>
public static class FileTimes
{
    /// <summary>
    /// Last-write time (UTC) of a file, or <see cref="DateTime.MinValue"/> if it vanished or access was
    /// denied. It then simply never wins the "most recent" tiebreak, never a crash. Slashes are normalised
    /// so a manifest-relative path (with '/') resolves the same as a walker path (with '\').
    /// </summary>
    public static DateTime SafeLastModifiedUtc(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path.Replace('/', '\\'));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }
}
