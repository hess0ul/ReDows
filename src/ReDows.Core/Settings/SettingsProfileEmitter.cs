using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReDows.Core.Settings;

/// <summary>
/// Renders a <see cref="SettingsProfile"/> to the two on-disk forms of the "settings half" of the
/// ReDows → InDows profile: settings-profile.json (the source of truth) and settings-profile.md (a
/// human-readable, module-grouped table). Pure and deterministic, so it is testable and shared by both
/// the CLI ('redows settings --by-module' / 'redows profile') and the GUI Apps screen — no duplication.
/// </summary>
public static class SettingsProfileEmitter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string RenderJson(SettingsProfile profile) => JsonSerializer.Serialize(profile, JsonOptions);

    public static string RenderMarkdown(SettingsProfile profile)
    {
        var text = new StringBuilder();
        text.AppendLine("# InDows settings profile (ReDows)");
        text.AppendLine();
        text.AppendLine("The settings ReDows read on this PC, grouped by the InDows module that re-applies them — the \"settings\" half of the ReDows → InDows profile.");

        text.AppendLine();
        text.AppendLine("Comes back automatically (an existing module re-applies it):");

        foreach (var module in profile.ExistingModules)
        {
            text.AppendLine();
            text.AppendLine($"## Module: {module.Module} ({module.Settings.Count})");
            text.AppendLine();
            text.AppendLine("| Setting | Value to apply |");
            text.AppendLine("| --- | --- |");
            foreach (var reading in module.Settings)
            {
                text.AppendLine($"| {Cell(reading.Definition.Name)} | {Cell(Desired(reading))} |");
            }
        }

        if (profile.ByBase.Count > 0)
        {
            text.AppendLine();
            text.AppendLine($"## Comes back via the InDows base install — not a module ({profile.ByBase.Count})");
            text.AppendLine();
            foreach (var reading in profile.ByBase)
            {
                text.AppendLine($"- {Cell(reading.Definition.Name)}: {Cell(Desired(reading))}");
            }
        }

        if (profile.NewModules.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("## New InDows modules to build");
            text.AppendLine();
            foreach (var module in profile.NewModules)
            {
                text.AppendLine($"- **{Cell(module.Module)}** — {Cell(string.Join(", ", module.Settings.Select(s => s.Definition.Name)))}");
            }
        }

        if (profile.PersoOnly.Count > 0)
        {
            text.AppendLine();
            text.AppendLine($"## Private config only — the public InDows leaves the default ({profile.PersoOnly.Count})");
            text.AppendLine();
            foreach (var reading in profile.PersoOnly)
            {
                text.AppendLine($"- {Cell(reading.Definition.Name)}: {Cell(Desired(reading))}");
            }
        }

        if (profile.NotApplied.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("## Module is the home, but the line is OFF today (needs wiring to restore)");
            foreach (var module in profile.NotApplied)
            {
                text.AppendLine();
                text.AppendLine($"### {module.Module} — not applied yet ({module.Settings.Count})");
                text.AppendLine();
                foreach (var reading in module.Settings)
                {
                    text.AppendLine($"- {Cell(reading.Definition.Name)}: {Cell(Desired(reading))}");
                }
            }
        }

        if (profile.Manual.Count > 0)
        {
            text.AppendLine();
            text.AppendLine($"## Nothing restores this — redo by hand after reset ({profile.Manual.Count})");
            text.AppendLine();
            foreach (var reading in profile.Manual)
            {
                text.AppendLine($"- {Cell(reading.Definition.Name)}: {Cell(Desired(reading))}");
            }
        }

        if (profile.NotInLoop.Count > 0)
        {
            text.AppendLine();
            text.AppendLine($"## Read-only — no InDows module, capture only ({profile.NotInLoop.Count})");
            text.AppendLine();
            foreach (var reading in profile.NotInLoop.OrderBy(r => r.Definition.Name, StringComparer.OrdinalIgnoreCase))
            {
                text.AppendLine($"- {Cell(reading.Definition.Name)}: {Cell(Desired(reading))}");
            }
        }

        if (profile.Unreadable.Count > 0)
        {
            text.AppendLine();
            text.AppendLine($"## Unreadable ({profile.Unreadable.Count})");
            text.AppendLine();
            foreach (var reading in profile.Unreadable.OrderBy(r => r.Definition.Name, StringComparer.OrdinalIgnoreCase))
            {
                text.AppendLine($"- {Cell(reading.Definition.Name)}: {Cell(reading.Error)}");
            }
        }

        return text.ToString();
    }

    /// <summary>The value InDows should re-apply: the current value, or the default when absent.</summary>
    private static string Desired(SettingReading reading) =>
        reading.Present ? $"{reading.RawValue} ({reading.Meaning})" : reading.Meaning;

    /// <summary>
    /// Markdown-table-safe cell: no pipes, single line, no control/bidi characters (an on-disk-derived
    /// value can carry them). Mirrors the CLI's ConsoleText sanitizer, then turns '|' into '/'.
    /// </summary>
    private static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        var chars = value.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (c == '|')
            {
                chars[i] = '/';
            }
            else if (char.IsControl(c) || c is >= (char)0x202A and <= (char)0x202E || c is >= (char)0x2066 and <= (char)0x2069)
            {
                chars[i] = '?'; // C0/DEL/C1 controls, bidi embeddings/overrides, bidi isolates
            }
        }

        return new string(chars);
    }
}
