namespace ReDows.Gui;

/// <summary>
/// Byte-size formatting for the GUI. Forwards to <see cref="ReDows.Core.Format"/>, the single
/// source of truth shared with the CLI, so the two front-ends can never drift. Kept as a thin
/// alias so the many existing <c>Format.Bytes(...)</c> call sites in the GUI stay unchanged.
/// </summary>
public static class Format
{
    public static string Bytes(long bytes) => ReDows.Core.Format.Bytes(bytes);

    public static string Gigabytes(long bytes) => ReDows.Core.Format.Gigabytes(bytes);
}
