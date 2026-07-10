using ReDows.Gui.Scanning;
using ReDows.Gui.ViewModels;

namespace ReDows.Gui.Reviewing;

/// <summary>
/// Turns the scan's recognized APP zones (each installed app + the map of everywhere its data was found)
/// into the "sort by app first" panel: one group per app, its paths as locations. Pure and read-only.
/// It reuses the zone's own colour and note as the per-location keep/drop
/// suggestion (a starting point, never a decision). Non-app zones (shipped memory: AppData, node_modules)
/// are skipped: this panel is about apps.
/// </summary>
public static class AppReviewGrouping
{
    public static IReadOnlyList<AppReviewGroupViewModel> Build(IReadOnlyList<RecognizedZoneRow> appZones) =>
        appZones
            .Where(zone => zone.IsApp && zone.Paths.Count > 0)
            .OrderBy(zone => zone.Label, StringComparer.OrdinalIgnoreCase)
            .Select(zone => new AppReviewGroupViewModel(
                zone.Label,
                zone.Paths
                    .Select(path => new AppReviewLocationViewModel(
                        new EntryRow(LeafName(path), path, IsDirectory: true, Bytes: 0),
                        zone.Importance,
                        zone.Note))
                    .ToList()))
            .ToList();

    private static string LeafName(string path)
    {
        var trimmed = path.Replace('/', '\\').TrimEnd('\\');
        var cut = trimmed.LastIndexOf('\\');
        return cut < 0 ? trimmed : trimmed[(cut + 1)..];
    }
}
