using ReDows.Core.Rules;
using ReDows.Core.Scanning;

namespace ReDows.Core.Saves;

/// <summary>
/// Turns a parsed <see cref="LudusaviManifest"/> into save-capture zones for a concrete machine, PURELY
/// (no I/O): for every "save"-tagged, Windows-applicable location it resolves ludusavi's placeholders
/// (<c>&lt;winAppData&gt;</c>, <c>&lt;winDocuments&gt;</c>, <c>&lt;home&gt;</c>…) against each profile and
/// emits an <see cref="AppDataZone"/> (verdict <c>capture:user</c>).
/// <para>Why <see cref="AppDataZone"/>: it is ADDITIVE-only — the engine applies it AFTER the ruleset and
/// ONLY over a REVIEW verdict, so a save location can only make a file MORE kept (review → capture), never
/// less. A wrong manifest entry over-captures (benign) but can never lose or drop data. The scan runner
/// then keeps only the zones whose folder actually exists on this PC.</para>
/// <para>Locations rooted at the game's INSTALL dir (<c>&lt;base&gt;</c>/<c>&lt;root&gt;</c>/<c>&lt;game&gt;</c>)
/// are skipped here: we cannot know where a given game is installed without guessing, so they are left out
/// rather than pointed at the wrong place (a later increment could cross-reference the app inventory).</para>
/// </summary>
public static class LudusaviSaveZoneBuilder
{
    private const string SaveTag = "save";
    private static readonly char[] GlobChars = ['*', '?', '['];

    public static IReadOnlyList<AppDataZone> Build(LudusaviManifest manifest, ScanContext context)
    {
        var byPrefix = new Dictionary<string, AppDataZone>(StringComparer.OrdinalIgnoreCase);

        foreach (var profile in context.Profiles)
        {
            var vars = ResolvePlaceholders(profile, context);
            foreach (var location in manifest.Locations)
            {
                if (!location.Tags.Contains(SaveTag, StringComparer.OrdinalIgnoreCase))
                {
                    continue; // saves only (config/other tags are out of scope for this pack)
                }

                if (!WindowsApplicable(location.When))
                {
                    continue; // a Linux/macOS-only entry does not apply here
                }

                var substituted = Substitute(location.RawPath, vars);
                if (substituted is null)
                {
                    continue; // an unresolvable placeholder (the game's install dir, an unmodelled store) — skip
                }

                var prefix = ConcretePrefix(substituted);
                if (prefix is null || ScanPaths.Split(prefix).Length < 2)
                {
                    continue; // no concrete prefix, or too shallow to be a safe zone
                }

                byPrefix.TryAdd(prefix, new AppDataZone("ludusavi:" + Sanitize(location.Game), prefix, Verdict.CaptureUser));
            }
        }

        return byPrefix.Values.ToList();
    }

    /// <summary>The ludusavi placeholders ReDows can resolve, per profile. Unresolved ones stay in the path
    /// (so the location is skipped) rather than being guessed; per-store ids become a wildcard segment.</summary>
    private static Dictionary<string, string> ResolvePlaceholders(UserProfileInfo profile, ScanContext context)
    {
        var vars = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string token, string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                vars[token] = value;
            }
        }

        Add("<home>", profile.RootPath);
        Add("<osUserName>", profile.UserName);
        Add("<winAppData>", profile.Environment.GetValueOrDefault("AppData"));
        Add("<winLocalAppData>", profile.Environment.GetValueOrDefault("LocalAppData"));
        Add("<winLocalAppDataLow>", string.IsNullOrEmpty(profile.RootPath) ? null : profile.RootPath + "/AppData/LocalLow");
        Add("<winDocuments>", profile.KnownFolders.GetValueOrDefault("Documents"));
        Add("<winPublic>", context.MachineEnvironment.GetValueOrDefault("Public"));
        Add("<winProgramData>", context.MachineEnvironment.GetValueOrDefault("ProgramData"));
        Add("<winDir>", context.MachineEnvironment.GetValueOrDefault("SystemRoot"));
        vars["<storeUserId>"] = "*"; // any user id under the store folder
        vars["<storeGameId>"] = "*"; // any store-specific game id
        return vars;
    }

    private static bool WindowsApplicable(IReadOnlyList<(string? Os, string? Store)> when)
    {
        // No condition = every OS. Otherwise it applies on Windows if any condition targets windows or is
        // store-only (a condition with no os matches any OS).
        if (when.Count == 0)
        {
            return true;
        }

        foreach (var (os, _) in when)
        {
            if (os is null || os.Equals("windows", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string? Substitute(string rawPath, IReadOnlyDictionary<string, string> vars)
    {
        var path = rawPath;
        foreach (var (token, value) in vars)
        {
            if (path.Contains(token, StringComparison.Ordinal))
            {
                path = path.Replace(token, value, StringComparison.Ordinal);
            }
        }

        // A leftover "<...>" is an unresolved placeholder (the game's install dir, an unmodelled store):
        // we don't know where it is, so the location is skipped rather than guessed.
        return path.Contains('<') ? null : path;
    }

    /// <summary>The path down to (but excluding) the first wildcard segment — the concrete folder to capture.</summary>
    private static string? ConcretePrefix(string path)
    {
        var kept = new List<string>();
        foreach (var segment in ScanPaths.Split(path))
        {
            if (segment.IndexOfAny(GlobChars) >= 0)
            {
                break;
            }

            kept.Add(segment);
        }

        return kept.Count == 0 ? null : string.Join('/', kept);
    }

    private static string Sanitize(string game) => game.Replace(':', '-'); // the "ludusavi:" prefix already carries the id namespace
}
