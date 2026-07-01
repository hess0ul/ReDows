using ReDows.Core.Duplicates;
using ReDows.Providers.Windows;

namespace ReDows.Cli;

/// <summary>
/// 'redows duplicates' — walk the machine (read-only) and report byte-identical files so the user can
/// reclaim space by keeping a single copy. It PROPOSES: it never deletes or modifies anything. Exit
/// codes: 0 ok, 2 usage, 4 unexpected error.
/// </summary>
public static class DuplicatesCommand
{
    private const int TopGroups = 25;

    public static int Run(string[] options)
    {
        try
        {
            return RunCore(options);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Unexpected error: {ex.GetType().Name}: {ex.Message}");
            return 4;
        }
    }

    private static int RunCore(string[] options)
    {
        string? root = null;
        long minSize = 1;

        const string usage = "Usage: redows duplicates [--root <path>] [--min-size <bytes|1KB|10MB|1GB>]";
        for (var i = 0; i < options.Length; i++)
        {
            switch (options[i])
            {
                case "--root" when i + 1 < options.Length:
                    root = options[++i];
                    break;
                case "--min-size" when i + 1 < options.Length:
                    if (!TryParseSize(options[++i], out minSize))
                    {
                        Console.Error.WriteLine($"--min-size must be a positive size (bytes, or like 500KB / 10MB / 1GB). {usage}");
                        return 2;
                    }

                    break;
                default:
                    Console.Error.WriteLine($"Invalid option '{options[i]}'. {usage}");
                    return 2;
            }
        }

        if (root is not null && !Directory.Exists(root))
        {
            Console.Error.WriteLine($"--root '{root}' is not an existing directory.");
            return 2;
        }

        var windowsContext = WindowsScanContextProvider.Build();
        var roots = root is null
            ? windowsContext.Context.Volumes.Select(v => v.RootPath).ToList()
            : [Path.GetFullPath(root)];

        Console.Error.WriteLine(root is null
            ? $"Scanning {roots.Count} volume(s) for duplicate files (read-only)…"
            : $"Scanning '{root}' for duplicate files (read-only)…");

        var walker = new WindowsFileSystemWalker();
        var files = new List<FileRef>();
        long seen = 0;
        foreach (var scanRoot in roots)
        {
            foreach (var entry in walker.Walk(scanRoot))
            {
                if (entry.Error is null && !entry.IsDirectory)
                {
                    files.Add(new FileRef(entry.Path, entry.SizeBytes));
                }

                if (++seen % 100_000 == 0)
                {
                    Console.Error.WriteLine($"  … {seen:N0} items seen");
                }
            }
        }

        Console.Error.WriteLine($"{files.Count:N0} files collected — hashing same-size candidates…");

        long hashed = 0;
        var groups = DuplicateFinder.Find(files, new Sha256FileHasher(), minSize, onFullHash: _ =>
        {
            if (++hashed % 2_000 == 0)
            {
                Console.Error.WriteLine($"  … {hashed:N0} files hashed");
            }
        });

        Console.WriteLine(Render(files.Count, minSize, groups));
        return 0;
    }

    private static string Render(int filesConsidered, long minSize, IReadOnlyList<DuplicateGroup> groups)
    {
        var text = new System.Text.StringBuilder();
        var reclaimable = groups.Sum(g => g.ReclaimableBytes);
        var extraCopies = groups.Sum(g => g.Count - 1);

        text.AppendLine("== ReDows duplicate files ==");
        text.AppendLine($"files considered: {filesConsidered:N0} (empty files and anything under {Bytes(minSize)} skipped)");
        text.AppendLine($"duplicate groups: {groups.Count:N0} (byte-identical, confirmed by SHA-256)");
        text.AppendLine($"extra copies: {extraCopies:N0}");
        text.AppendLine($"reclaimable space: {Bytes(reclaimable)} (if one copy of each is kept)");

        if (groups.Count > 0)
        {
            text.AppendLine();
            text.AppendLine($"== Top duplicate groups (by reclaimable space, showing {Math.Min(TopGroups, groups.Count)} of {groups.Count:N0}) ==");
            foreach (var group in groups.Take(TopGroups))
            {
                text.AppendLine(
                    $"  {Bytes(group.ReclaimableBytes)} reclaimable · {group.Count} copies · {Bytes(group.Size)} each");
                foreach (var path in group.Paths)
                {
                    text.AppendLine($"      {ConsoleText.Sanitize(path)}");
                }
            }
        }

        text.AppendLine();
        text.AppendLine("Read-only: nothing was deleted or modified. Review each group and decide for yourself.");
        return text.ToString();
    }

    private static bool TryParseSize(string text, out long bytes)
    {
        bytes = 0;
        var trimmed = text.Trim().ToUpperInvariant();
        var multiplier = 1L;
        foreach (var (suffix, factor) in new[] { ("GB", 1L << 30), ("MB", 1L << 20), ("KB", 1L << 10), ("B", 1L) })
        {
            if (trimmed.EndsWith(suffix, StringComparison.Ordinal))
            {
                multiplier = factor;
                trimmed = trimmed[..^suffix.Length].Trim();
                break;
            }
        }

        if (!double.TryParse(trimmed, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) || value < 0)
        {
            return false;
        }

        bytes = Math.Max(1, (long)(value * multiplier));
        return true;
    }

    private static string Bytes(long bytes) => bytes switch
    {
        >= 1L << 40 => $"{bytes / (double)(1L << 40):F2} TB",
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F2} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F2} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F1} KB",
        _ => $"{bytes} B",
    };
}
