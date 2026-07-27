namespace ReDows.Core.Scanning;

/// <summary>
/// Everything the classifier needs to instantiate rules on a concrete machine:
/// machine environment, volumes, user profiles (with their own environment and
/// Known Folders, resolved per profile and never from the calling session).
/// On a real PC this is produced by ReDows.Providers.Windows; tests build it
/// by hand around a simulated file system.
/// </summary>
public sealed record ScanContext(
    IReadOnlyDictionary<string, string> MachineEnvironment,
    IReadOnlyList<VolumeInfo> Volumes,
    IReadOnlyList<UserProfileInfo> Profiles,
    IReadOnlyList<string>? OrphanProfileDirectories = null)
{
    /// <summary>
    /// Directories under the profiles root that belong to no ProfileList entry
    /// (deleted accounts with kept files, migrations). This is user data off the radar,
    /// surfaced as high-priority REVIEW (deny-list §A.7/§C-21).
    /// </summary>
    public IReadOnlyList<string> Orphans => OrphanProfileDirectories ?? [];
}

public sealed record VolumeInfo(
    string RootPath,
    string? VolumeGuid = null,
    IReadOnlyList<string>? MountPaths = null,
    string? Label = null,
    string? FileSystemFormat = null,
    long? TotalBytes = null);

/// <summary>
/// One real user profile (from ProfileList). When the profile's registry hive
/// could not be read (other user, no elevation), <paramref name="HiveResolved"/>
/// is false and Environment/KnownFolders only contain certain values: rules
/// depending on missing tokens are then NOT instantiated for this profile
/// (asymmetric policy: an ignore rule must never run on a guessed path) and the
/// profile's tree falls to counted REVIEW.
/// </summary>
public sealed record UserProfileInfo(
    string Sid,
    string UserName,
    string RootPath,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyDictionary<string, string> KnownFolders,
    bool HiveResolved = true);

/// <summary>
/// Minimal read-only view of a file system, used by context conditions
/// (ancestor markers). Returns child entry names of a directory; an unknown
/// or non-directory path yields an empty sequence.
/// </summary>
public interface IFileSystemView
{
    IEnumerable<string> EnumerateEntryNames(string directoryPath);
}

/// <summary>Path helpers shared by the engine. Paths are segment lists; separators '/' and '\' are equivalent.</summary>
public static class ScanPaths
{
    private static readonly char[] Separators = ['/', '\\'];

    public static string[] Split(string path) =>
        path.Split(Separators, StringSplitOptions.RemoveEmptyEntries);

    public static string Normalize(string path) => string.Join('/', Split(path));

    /// <summary>
    /// Anchors a bare drive segment ("C:") to its root ("C:\"). On Windows a
    /// bare drive path is drive-RELATIVE. It silently resolves to the process
    /// current directory, retargeting any API it is passed to.
    /// </summary>
    public static string AnchorDriveRoot(string path) =>
        path.Length >= 2 && path[^1] == ':' ? path + '\\' : path;

    /// <summary>
    /// True if <paramref name="path"/> is an absolute Windows path — a drive-letter
    /// root ("C:\" or "C:/") or a UNC root ("\\server"). Unlike <c>Path.IsPathRooted</c>,
    /// which only recognises drive letters and backslashes when the host OS is Windows,
    /// this is OS-independent, so the engine classifies the same Windows paths identically
    /// on any host (e.g. Linux CI). Drive-RELATIVE paths ("C:foo", no separator) and
    /// relative paths ("..\x") are not rooted.
    /// </summary>
    public static bool IsRootedWindowsPath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        // UNC: two leading separators ("\\server\share").
        if (path.Length >= 2 && IsSeparator(path[0]) && IsSeparator(path[1]))
        {
            return true;
        }

        // Drive-letter absolute: a letter, ':', then a separator ("C:\" / "C:/").
        return path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && IsSeparator(path[2]);
    }

    private static bool IsSeparator(char c) => c is '/' or '\\';
}
