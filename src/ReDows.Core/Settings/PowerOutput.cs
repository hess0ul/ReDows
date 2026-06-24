using System.Globalization;

namespace ReDows.Core.Settings;

/// <summary>Pure parsing of <c>powercfg</c> text output. No process launch — testable on fixtures.</summary>
public static class PowerOutput
{
    /// <summary>The active scheme's friendly name, from "Power Scheme GUID: &lt;guid&gt;  (Name)".</summary>
    public static string? ActiveSchemeName(string? output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return null;
        }

        var open = output.IndexOf('(');
        var close = output.LastIndexOf(')');
        return open >= 0 && close > open ? output[(open + 1)..close].Trim() : null;
    }

    /// <summary>The "Current AC Power Setting Index: 0x…" value as a decimal string (e.g. 1800 seconds).</summary>
    public static string? AcSettingIndex(string? output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return null;
        }

        const string marker = "Current AC Power Setting Index:";
        foreach (var line in output.Split('\n'))
        {
            var at = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                continue;
            }

            var token = line[(at + marker.Length)..].Trim();
            if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                && uint.TryParse(token[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            {
                return value.ToString(CultureInfo.InvariantCulture);
            }
        }

        return null;
    }
}
