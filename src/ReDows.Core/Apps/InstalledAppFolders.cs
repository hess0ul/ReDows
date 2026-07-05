using System.Text;

namespace ReDows.Core.Apps;

/// <summary>
/// Recognises a folder made by an INSTALLED application, by matching its name against the app inventory
/// (each app's name and its publisher). So a "ShareX", "Adobe" or "Rockstar Games" folder — wherever it
/// sits — is known to be the software's data, not something the user created, WITHOUT hand-listing every
/// app. Pure and conservative: it matches the WHOLE normalised folder name against a whole app/publisher
/// name (a light corporate-suffix strip aside), so a chance match stays rare, and callers colour such a
/// folder "review" (forget-nothing), never keep or drop.
/// </summary>
public sealed class InstalledAppFolders
{
    // These are true corporate designators — safe to strip so "Adobe Inc." matches an "Adobe" folder.
    // NOT "Games"/"Software"/"Studios"/"Entertainment": those are part of distinctive names (Rockstar Games).
    private static readonly HashSet<string> CorporateSuffixes = new(StringComparer.Ordinal)
    {
        "inc", "llc", "ltd", "limited", "corp", "corporation", "gmbh", "co", "company",
        "sa", "srl", "ag", "bv", "oy", "ab", "kk", "pty", "plc",
    };

    private readonly Dictionary<string, string> _byNormalized; // normalised name/publisher → display label

    public InstalledAppFolders(IEnumerable<(string? Name, string? Publisher)> apps)
    {
        _byNormalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, publisher) in apps)
        {
            Add(name);
            Add(publisher);
        }
    }

    /// <summary>How many distinct app/publisher names are known (0 = nothing to match against).</summary>
    public int Count => _byNormalized.Count;

    /// <summary>The installed app/publisher whose folder this is, or null if the name matches none.</summary>
    public string? Match(string folderName)
    {
        var key = Normalize(folderName);
        return key.Length >= 3 && _byNormalized.TryGetValue(key, out var label) ? label : null;
    }

    private void Add(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        var key = Normalize(raw);
        if (key.Length >= 3)
        {
            _byNormalized.TryAdd(key, raw.Trim());
        }
    }

    private static string Normalize(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
            }
            else if (ch is ' ' or '-' or '_' or '.')
            {
                sb.Append(' ');
            }
        }

        var tokens = sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (tokens.Count > 1 && CorporateSuffixes.Contains(tokens[^1]))
        {
            tokens.RemoveAt(tokens.Count - 1);
        }

        return string.Join(' ', tokens);
    }
}
