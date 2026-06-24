namespace ReDows.Core.Settings;

/// <summary>The settings an InDows module should re-apply, with their captured values.</summary>
public sealed record ModuleSettings(string Module, IReadOnlyList<SettingReading> Settings);

/// <summary>
/// The "settings half" of the ReDows → InDows profile. Each in-loop setting is filed by HOW
/// it comes back after a reset, so the profile is honest about what restores automatically
/// versus what the user must redo:
/// <list type="bullet">
///   <item><see cref="ExistingModules"/> — an existing InDows module re-applies it (automatic).</item>
///   <item><see cref="NewModules"/> — a module still to build (<c>NEW:</c> prefix).</item>
///   <item><see cref="ByBase"/> — the InDows base install applies it, not a module (automatic).</item>
///   <item><see cref="PersoOnly"/> — only the private config applies it; the public InDows leaves the default.</item>
///   <item><see cref="NotApplied"/> — a module is the intended home but its line is off today (needs wiring).</item>
///   <item><see cref="Manual"/> — nothing applies it; redo by hand after reset.</item>
/// </list>
/// plus <see cref="NotInLoop"/> (capture-only, no module) and <see cref="Unreadable"/>.
/// </summary>
public sealed record SettingsProfile(
    IReadOnlyList<ModuleSettings> ExistingModules,
    IReadOnlyList<ModuleSettings> NewModules,
    IReadOnlyList<SettingReading> ByBase,
    IReadOnlyList<SettingReading> PersoOnly,
    IReadOnlyList<ModuleSettings> NotApplied,
    IReadOnlyList<SettingReading> Manual,
    IReadOnlyList<SettingReading> NotInLoop,
    IReadOnlyList<SettingReading> Unreadable);

/// <summary>Pure mapping of a <see cref="SettingsReport"/> to a <see cref="SettingsProfile"/>.</summary>
public static class SettingsProfileBuilder
{
    private const string NewPrefix = "NEW:";

    // Sentinel indows_module values: the setting comes back from somewhere other than a module.
    private const string BaseSentinel = "base";
    private const string PersoSentinel = "perso";
    private const string NoneSentinel = "none";

    public static SettingsProfile Build(SettingsReport report)
    {
        var unreadable = report.Readings.Where(r => r.Error is not null).ToList();
        var readable = report.Readings.Where(r => r.Error is null).ToList();

        var notInLoop = readable.Where(r => !r.Definition.InLoop).ToList();
        var inLoop = readable.Where(r => r.Definition.InLoop).ToList();

        var byBase = Sorted(inLoop.Where(r => IsSentinel(r, BaseSentinel)));
        var persoOnly = Sorted(inLoop.Where(r => IsSentinel(r, PersoSentinel)));
        var manual = Sorted(inLoop.Where(r => IsSentinel(r, NoneSentinel)));

        // Whatever a real (or NEW:) module owns — split applied vs not-yet-applied.
        var module = inLoop.Where(r => !IsAnySentinel(r)).ToList();
        var notApplied = GroupByModule(module.Where(r => !r.Definition.Applied));
        var applied = module.Where(r => r.Definition.Applied).ToList();

        var existing = GroupByModule(applied.Where(r => !IsNew(r)));
        var pending = GroupByModule(applied.Where(IsNew));

        return new SettingsProfile(existing, pending, byBase, persoOnly, notApplied, manual, notInLoop, unreadable);
    }

    private static bool IsSentinel(SettingReading reading, string sentinel) =>
        string.Equals(reading.Definition.IndowsModule, sentinel, StringComparison.OrdinalIgnoreCase);

    private static bool IsAnySentinel(SettingReading reading) =>
        IsSentinel(reading, BaseSentinel) || IsSentinel(reading, PersoSentinel) || IsSentinel(reading, NoneSentinel);

    private static bool IsNew(SettingReading reading) =>
        reading.Definition.IndowsModule?.StartsWith(NewPrefix, StringComparison.OrdinalIgnoreCase) ?? false;

    private static IReadOnlyList<SettingReading> Sorted(IEnumerable<SettingReading> readings) =>
        readings.OrderBy(r => r.Definition.Name, StringComparer.OrdinalIgnoreCase).ToList();

    private static IReadOnlyList<ModuleSettings> GroupByModule(IEnumerable<SettingReading> readings) =>
        readings
            .GroupBy(r => ModuleName(r.Definition.IndowsModule), StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ModuleSettings(g.Key, Sorted(g)))
            .ToList();

    private static string ModuleName(string? indowsModule)
    {
        if (string.IsNullOrWhiteSpace(indowsModule))
        {
            return "(unmapped)";
        }

        return indowsModule.StartsWith(NewPrefix, StringComparison.OrdinalIgnoreCase)
            ? indowsModule[NewPrefix.Length..]
            : indowsModule;
    }
}
