namespace ReDows.Core;

/// <summary>
/// Human-readable byte sizes — the single source of truth shared by the CLI and the GUI so the
/// two front-ends can never drift (they used to each carry their own copy of these switches).
/// </summary>
public static class Format
{
    /// <summary>Adaptive binary size (TB/GB/MB/KB/B): the largest unit that keeps the number readable.</summary>
    public static string Bytes(long bytes) => bytes switch
    {
        >= 1L << 40 => $"{bytes / (double)(1L << 40):F2} TB",
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F2} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F2} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F1} KB",
        _ => $"{bytes} B",
    };

    /// <summary>Whole-volume size as a single "N.N GB" figure (a disk reads better in one fixed unit).</summary>
    public static string Gigabytes(long bytes) => $"{bytes / (double)(1L << 30):F1} GB";
}
