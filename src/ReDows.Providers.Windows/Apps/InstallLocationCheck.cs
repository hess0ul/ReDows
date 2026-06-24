namespace ReDows.Providers.Windows.Apps;

/// <summary>
/// Three-way install-location check (capture framework §F-b): a missing path on
/// a MOUNTED volume is probably a stale manifest (review), but a path on an
/// ABSENT volume is an alert — a dead path is not lost data, the volume must be
/// plugged back before the reset.
/// </summary>
public static class InstallLocationCheck
{
    public const string VolumeAbsentAlert =
        "ALERT: volume absent — plug it back before the reset (entry kept, nothing dropped)";

    public const string PathMissingNote =
        "install location missing on a mounted volume — stale entry? review";

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
