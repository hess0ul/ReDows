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

    // Windows containers that hold MANY apps, not one game's saves: if a save path wildcards away the
    // per-app segment (e.g. "<winLocalAppData>/Packages/&lt;storeGameId&gt;/…" → "…/Local/Packages"),
    // the prefix truncates to the shared root and would sweep every Store app. Reject such a leaf.
    // "My Games" is deliberately NOT here — it holds game saves only, so capturing it is on-target.
    private static readonly HashSet<string> SharedContainers = new(StringComparer.OrdinalIgnoreCase) { "Packages" };

    public static IReadOnlyList<AppDataZone> Build(LudusaviManifest manifest, ScanContext context)
    {
        var byPrefix = new Dictionary<string, AppDataZone>(StringComparer.OrdinalIgnoreCase);

        foreach (var profile in context.Profiles)
        {
            var vars = ResolvePlaceholders(profile, context);
            // The resolved Known-Folder roots (AppData, Documents, home, LocalAppData, Public…). A zone must
            // be a proper DESCENDANT of one of them — a game-specific subfolder — never the bare root: a path
            // like "<winAppData>/*" would otherwise capture the WHOLE Roaming folder (caught by a real smoke).
            var roots = vars.Values
                .Where(v => v != "*")
                .Select(ScanPaths.Normalize)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

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
                if (prefix is null || !IsGameSpecificSubfolder(prefix, roots))
                {
                    continue; // no concrete prefix, or it is a bare Known-Folder root (would over-capture)
                }

                var leaf = ScanPaths.Split(prefix)[^1];
                if (SharedContainers.Contains(leaf))
                {
                    continue; // a multi-app system container (Packages…) — the per-game segment was wildcarded away
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

    /// <summary>
    /// True when <paramref name="prefix"/> is at least one segment BELOW the deepest Known-Folder root it
    /// sits under — i.e. a game-specific subfolder, not a bare root. A save path whose first segment after
    /// the placeholder is a wildcard truncates back to the root itself (e.g. "&lt;winAppData&gt;/*" →
    /// "…/Roaming"); capturing that would sweep the whole folder, so it is rejected.
    /// </summary>
    private static bool IsGameSpecificSubfolder(string prefix, IReadOnlyList<string> roots)
    {
        var depth = ScanPaths.Split(prefix).Length;
        var deepestRoot = 0;
        foreach (var root in roots)
        {
            if (IsUnderOrEqual(prefix, root))
            {
                deepestRoot = Math.Max(deepestRoot, ScanPaths.Split(root).Length);
            }
        }

        return deepestRoot > 0 && depth > deepestRoot;
    }

    private static bool IsUnderOrEqual(string path, string root) =>
        path.Length >= root.Length
        && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
        && (path.Length == root.Length || path[root.Length] == '/');

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
