namespace ReDows.Cli;

/// <summary>
/// The single sanitizer for every on-disk-derived string printed to a terminal
/// (paths, file names, user names, volume labels, registry values). NTFS allows
/// control characters in names and a VT-capable terminal interprets C0, DEL, C1
/// (0x9B is a one-byte CSI introducer) — escape-sequence injection — while the
/// Unicode bidi controls can visually reorder a path (spoofing).
/// </summary>
public static class ConsoleText
{
    public static string Sanitize(string text)
    {
        if (!text.Any(NeedsReplacement))
        {
            return text;
        }

        var chars = text.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (NeedsReplacement(chars[i]))
            {
                chars[i] = '?';
            }
        }

        return new string(chars);
    }

    private static bool NeedsReplacement(char c) =>
        char.IsControl(c) // C0, DEL, C1 (incl. 0x9B CSI)
        || c is >= '\u202A' and <= '\u202E' // bidi embeddings/overrides (LRE..RLO)
        || c is >= '\u2066' and <= '\u2069'; // bidi isolates (LRI..PDI)
}
