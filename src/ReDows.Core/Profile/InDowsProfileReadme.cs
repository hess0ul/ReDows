using System.Text;
using ReDows.Core.Apps;
using ReDows.Core.Settings;

namespace ReDows.Core.Profile;

/// <summary>
/// Pure renderer of the human-facing README that tops the InDows profile folder: a
/// plain-language summary of what InDows restores automatically and — the point for
/// forget-nothing — what it will NOT, so the user keeps a redo-by-hand checklist.
/// </summary>
public static class InDowsProfileReadme
{
    public static string Render(InDowsCatalog catalog, SettingsProfile profile)
    {
        var text = new StringBuilder();
        text.AppendLine("# InDows profile (produced by ReDows)");
        text.AppendLine();
        text.AppendLine("Drop this folder into InDows to rebuild this PC. `configuration.dsc.yaml` feeds the winget");
        text.AppendLine("install loop; `settings-profile.json`/`.md` carry the settings to re-apply, grouped by module.");
        text.AppendLine();

        text.AppendLine("## Apps");
        text.AppendLine();
        text.AppendLine($"- **{catalog.ActiveCount}** ready to reinstall via winget (in `configuration.dsc.yaml`).");
        text.AppendLine($"- **{catalog.CandidateCount}** uncertain + **{catalog.ManualCount}** without a winget id — left as comments to review by hand.");
        text.AppendLine();

        text.AppendLine("## Settings — InDows modules to enable (come back automatically)");
        text.AppendLine();
        if (profile.ExistingModules.Count > 0)
        {
            foreach (var module in profile.ExistingModules)
            {
                text.AppendLine($"- `{Clean(module.Module)}` ({module.Settings.Count})");
            }
        }
        else
        {
            text.AppendLine("- (none)");
        }

        if (profile.ByBase.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Also applied automatically by the InDows base install — no action:");
            foreach (var reading in profile.ByBase)
            {
                text.AppendLine($"- {Name(reading)}");
            }
        }

        if (profile.NewModules.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("## InDows modules still to build");
            text.AppendLine();
            foreach (var module in profile.NewModules)
            {
                text.AppendLine($"- `{Clean(module.Module)}` — {Names(module)}");
            }
        }

        text.AppendLine();
        text.AppendLine("## ⚠️ Won't come back on its own — redo-by-hand checklist");
        text.AppendLine();
        var anyManualWork = profile.Manual.Count + profile.NotApplied.Count + profile.PersoOnly.Count + profile.Unreadable.Count;
        if (anyManualWork == 0)
        {
            text.AppendLine("Nothing here — every captured setting is restored automatically.");
        }

        if (profile.Manual.Count > 0)
        {
            text.AppendLine($"**Nothing restores these — set them by hand ({profile.Manual.Count}):**");
            text.AppendLine();
            foreach (var reading in profile.Manual)
            {
                text.AppendLine($"- {Name(reading)}: {Desired(reading)}");
            }

            text.AppendLine();
        }

        if (profile.NotApplied.Count > 0)
        {
            text.AppendLine("**A module is the home, but its line is OFF today — wire it up or set by hand:**");
            text.AppendLine();
            foreach (var module in profile.NotApplied)
            {
                foreach (var reading in module.Settings)
                {
                    text.AppendLine($"- {Name(reading)} (`{Clean(module.Module)}`): {Desired(reading)}");
                }
            }

            text.AppendLine();
        }

        if (profile.PersoOnly.Count > 0)
        {
            text.AppendLine($"**Only your private config applies these — a public InDows leaves the default ({profile.PersoOnly.Count}):**");
            text.AppendLine();
            foreach (var reading in profile.PersoOnly)
            {
                text.AppendLine($"- {Name(reading)}: {Desired(reading)}");
            }

            text.AppendLine();
        }

        if (profile.Unreadable.Count > 0)
        {
            text.AppendLine($"**Could not be read (check by hand) ({profile.Unreadable.Count}):**");
            text.AppendLine();
            foreach (var reading in profile.Unreadable)
            {
                text.AppendLine($"- {Name(reading)}: {Clean(reading.Error)}");
            }

            text.AppendLine();
        }

        return text.ToString();
    }

    private static string Name(SettingReading reading) => Clean(reading.Definition.Name);

    /// <summary>The value InDows should re-apply: the current value, or the default when absent.</summary>
    private static string Desired(SettingReading reading) =>
        Clean(reading.Present ? $"{reading.RawValue} ({reading.Meaning})" : reading.Meaning);

    private static string Names(ModuleSettings module) =>
        Clean(string.Join(", ", module.Settings.Select(s => s.Definition.Name)));

    private static string Clean(string? value) =>
        string.IsNullOrEmpty(value) ? "" : value.Replace('\r', ' ').Replace('\n', ' ');
}
