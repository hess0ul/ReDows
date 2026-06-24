using System.Diagnostics;

namespace ReDows.Providers.Windows.Settings;

/// <summary>
/// Runs one read-only PowerShell query and returns (output, failureReason): a null
/// output means the query failed, with a concise reason (elevation / timeout / exit).
/// Shared by the feature and network settings readers. Streams are drained
/// concurrently to avoid a full-buffer deadlock; the query is bounded by a timeout.
/// </summary>
internal static class PowerShellQuery
{
    public static (string? Output, string? Reason) Run(string command)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -Command \"{command}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(120_000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // The timed-out process may have exited on its own — best effort.
                }

                return (null, "query timed out");
            }

            var error = stderr.GetAwaiter().GetResult();
            return process.ExitCode != 0
                ? (null, Summarize(error, process.ExitCode))
                : (stdout.GetAwaiter().GetResult(), null);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return (null, $"cannot run PowerShell ({ex.GetType().Name})");
        }
    }

    private static string Summarize(string stderr, int exitCode) =>
        stderr.Contains("requires elevation", StringComparison.OrdinalIgnoreCase)
            ? "requires elevation (re-run elevated to read this)"
            : $"query failed (exit {exitCode})";
}
