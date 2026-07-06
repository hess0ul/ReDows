namespace ReDows.Providers.Windows.Apps;

/// <summary>
/// Three-way install-location check (capture framework §F-b): a missing path on
/// a MOUNTED volume is probably a stale manifest (review), but a path on an
/// ABSENT volume is an alert. A dead path is not lost data; the volume must be
/// plugged back before the reset.
/// </summary>
public static class InstallLocationCheck
{
    public const string VolumeAbsentAlert =
        "Alert: volume absent. Plug it back before the reset. The entry is kept and nothing is dropped.";

    public const string PathMissingNote =
        "install location missing on a mounted volume. Possibly a stale entry; review.";

    public static string? Note(string? installLocation)
    {
        if (string.IsNullOrWhiteSpace(installLocation))
        {
            return null;
        }

        try
        {
            if (Directory.Exists(installLocation))
            {
                return null;
            }

            var root = Path.GetPathRoot(installLocation);
            return string.IsNullOrEmpty(root) || Directory.Exists(root)
                ? PathMissingNote
                : VolumeAbsentAlert;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return PathMissingNote;
        }
    }
}
