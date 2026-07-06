using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ReDows.Core.Saves;

/// <summary>
/// One save-file location declared by the ludusavi manifest for one game: the raw path (still holding
/// ludusavi placeholders like <c>&lt;winAppData&gt;</c> and glob wildcards), the tags on it (we care about
/// <c>save</c>), and the OS/store conditions under which it applies. Purely the parsed data. Resolving the
/// placeholders to real paths is the zone builder's job.
/// </summary>
public sealed record LudusaviSaveLocation(
    string Game,
    string RawPath,
    IReadOnlyList<string> Tags,
    IReadOnlyList<(string? Os, string? Store)> When);

/// <summary>
/// A parsed ludusavi manifest (github.com/mtkennerly/ludusavi-manifest), flattened to a list of
/// per-game save locations. The manifest DATA is compiled from PCGamingWiki (CC BY-NC-SA 3.0), so it is
/// never bundled in ReDows. It is downloaded onto the user's own machine and parsed here.
/// <para>Parsing is fail-SAFE: a missing, empty or malformed manifest yields an EMPTY manifest (the
/// feature simply does nothing), never an exception. Unlike ReDows' own shipped rules, this is optional
/// third-party data and a bad download must never break a scan.</para>
/// </summary>
public sealed class LudusaviManifest
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties() // the manifest also carries installDir/registry/steam/gog/launch, but we only read files
        .Build();

    public LudusaviManifest(IReadOnlyList<LudusaviSaveLocation> locations) => Locations = locations;

    public IReadOnlyList<LudusaviSaveLocation> Locations { get; }

    /// <summary>How many games appear in the manifest at least once (diagnostic).</summary>
    public int GameCount => Locations.Select(l => l.Game).Distinct(StringComparer.OrdinalIgnoreCase).Count();

    /// <summary>Parse a ludusavi manifest (YAML). Fail-safe: any error yields an empty manifest.</summary>
    public static LudusaviManifest Parse(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return new LudusaviManifest([]);
        }

        Dictionary<string, GameDto?>? games;
        try
        {
            games = Deserializer.Deserialize<Dictionary<string, GameDto?>>(yaml);
        }
        catch (YamlException)
        {
            return new LudusaviManifest([]); // a corrupt download disables the feature; it never crashes the scan
        }

        var locations = new List<LudusaviSaveLocation>();
        foreach (var (game, dto) in games ?? [])
        {
            if (string.IsNullOrWhiteSpace(game) || dto?.Files is null)
            {
                continue;
            }

            foreach (var (path, file) in dto.Files)
            {
                if (string.IsNullOrWhiteSpace(path) || file is null)
                {
                    continue;
                }

                var tags = (IReadOnlyList<string>)(file.Tags ?? []);
                var when = (file.When ?? []).Select(w => (w.Os, w.Store)).ToList();
                locations.Add(new LudusaviSaveLocation(game.Trim(), path.Trim(), tags, when));
            }
        }

        return new LudusaviManifest(locations);
    }

    private sealed class GameDto
    {
        public Dictionary<string, FileDto?>? Files { get; set; }
    }

    private sealed class FileDto
    {
        public List<string>? Tags { get; set; }

        public List<WhenDto>? When { get; set; }
    }

    private sealed class WhenDto
    {
        public string? Os { get; set; }

        public string? Store { get; set; }
    }
}
