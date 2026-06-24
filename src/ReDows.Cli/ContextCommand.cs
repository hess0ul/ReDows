using System.Text.Json;
using ReDows.Providers.Windows;

namespace ReDows.Cli;

/// <summary>'redows context show' — display the real machine's resolved ScanContext for human validation.</summary>
public static class ContextCommand
{
    public static int Show(bool asJson)
    {
        try
        {
            var windowsContext = WindowsScanContextProvider.Build();

            if (asJson)
            {
                Console.WriteLine(JsonSerializer.Serialize(
                    windowsContext, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }

            Render(windowsContext);
            return 0;
        }
        catch (Exception ex)
        {
            // Same exit-code contract as scan/apps: discovery failures must not
            // surface as a raw stack trace with an undocumented exit code.
            Console.Error.WriteLine($"Unexpected error: {ex.GetType().Name}: {ex.Message}");
            return 4;
        }
    }

    private static void Render(WindowsContext windowsContext)
    {
        var context = windowsContext.Context;

        Console.WriteLine("== Machine environment ==");
        foreach (var (name, value) in context.MachineEnvironment.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine($"  %{name}% = {S(value)}");
        }

        Console.WriteLine();
        Console.WriteLine($"== Volumes ({windowsContext.AllVolumes.Count} discovered, {context.Volumes.Count} in scan scope) ==");
        foreach (var volume in windowsContext.AllVolumes)
        {
            var mounts = volume.MountPaths.Count > 0 ? S(string.Join(", ", volume.MountPaths)) : "(no mount point)";
            var size = volume.TotalBytes is { } bytes ? $"{bytes / (1024.0 * 1024 * 1024):F1} GB" : "?";
            var status = volume.Scannable ? "scan" : $"excluded: {volume.ExclusionReason}";
            Console.WriteLine($"  [{status}] {mounts} — {volume.DriveKind}, {volume.FileSystemFormat ?? "?"}, {size}, label '{S(volume.Label ?? "")}'");
            Console.WriteLine($"         {volume.GuidPath}");
        }

        Console.WriteLine();
        Console.WriteLine($"== Profiles ({context.Profiles.Count} from ProfileList) ==");
        foreach (var profile in context.Profiles)
        {
            var hive = profile.HiveResolved ? "hive resolved" : "HIVE UNREAD — degraded (counted)";
            Console.WriteLine($"  {S(profile.UserName)}  ({profile.Sid})  [{hive}]");
            Console.WriteLine($"    root: {S(profile.RootPath)}");
            foreach (var (name, value) in profile.Environment.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine($"    %{S(name)}% = {S(value)}");
            }

            foreach (var (name, value) in profile.KnownFolders.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                var redirected = value.StartsWith(profile.RootPath, StringComparison.OrdinalIgnoreCase)
                    ? ""
                    : "   << redirected";
                Console.WriteLine($"    FOLDERID_{name} = {S(value)}{redirected}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"== Orphan profile directories ({context.Orphans.Count}) ==");
        foreach (var orphan in context.Orphans)
        {
            Console.WriteLine($"  {S(orphan)}   << user data outside ProfileList — high-priority REVIEW at scan time");
        }

        if (windowsContext.Notes.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"== Notes ({windowsContext.Notes.Count}) ==");
            foreach (var note in windowsContext.Notes)
            {
                Console.WriteLine($"  - {S(note)}");
            }
        }
    }

    /// <summary>Every on-disk/hive-derived string is sanitized before hitting the terminal.</summary>
    private static string S(string text) => ConsoleText.Sanitize(text);
}
