using System.Diagnostics;
using ReDows.Core.Apps;

namespace ReDows.Providers.Windows.Apps;

/// <summary>
/// OPT-IN winget enrichment (--enrich-winget) — an arbitrated read-only
/// deviation: running winget can persist source-agreement acceptance, write
/// state under the user profile and touch the network, so it NEVER runs by
/// default and never elevated. `winget export` yields a corroboration list of
/// reinstallable package ids (matched apps only, ids without names) — dynamic
/// sources are corroboration only; row-level id attachment via winget's own
/// correlation engine (COM InstalledCatalog) is the planned next step.
/// </summary>
public static class WingetEnrichment
{
    public const string SourceName = "winget";

    public sealed record WingetResult(
        IReadOnlyList<WingetExport.ExportedPackage> ExportedIds,
        InventoryDegradation? Degradation,
        string? Note);

    public static WingetResult Run()
    {
        var alias = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", "winget.exe");
        if (!File.Exists(alias))
        {
            return new WingetResult([], new InventoryDegradation(SourceName, "this machine",
                "winget.exe not found (absent, or app execution aliases disabled) — no winget ids"), null);
        }

        var exportFile = Path.Combine(Path.GetTempPath(), $"redows-winget-export-{Guid.NewGuid():N}.json");
        try
        {
            using var process = Process.Start(new ProcessStartInfo(alias)
            {
                ArgumentList = { "export", "-o", exportFile, "--accept-source-agreements", "--disable-interactivity" },
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null)
            {
                return new WingetResult([], new InventoryDegradation(SourceName, "this machine",
                    "winget could not be started"), null);
            }

            // Drain one redirected stream asynchronously while reading the other:
            // two sequential ReadToEnd() calls deadlock if the child fills the
            // stderr pipe buffer while we are still blocked on stdout.
            _ = process.StandardError.ReadToEndAsync();
            process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(120_000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // already exited
                }

                return new WingetResult([], new InventoryDegradation(SourceName, "this machine",
                    "winget export timed out after 120s"), null);
            }

            if (!File.Exists(exportFile))
            {
                return new WingetResult([], new InventoryDegradation(SourceName, "this machine",
                    $"winget export produced no file (exit code 0x{process.ExitCode:X8})"), null);
            }

            // Non-zero exit with a valid file is NORMAL: apps with no source
            // match are warned about and omitted — partial success by design.
            var ids = WingetExport.Parse(File.ReadAllText(exportFile));
            var note = process.ExitCode == 0
                ? $"winget export: {ids.Count} package id(s) matched"
                : $"winget export: {ids.Count} package id(s) matched; some installed apps had no source match (exit 0x{process.ExitCode:X8}) — they stay reinstall:manual";
            return new WingetResult(ids, null, note);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new WingetResult([], new InventoryDegradation(SourceName, "this machine",
                $"winget enrichment failed: {ex.GetType().Name}: {ex.Message}"), null);
        }
        finally
        {
            try
            {
                File.Delete(exportFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup of our own temp artifact: a locked or
                // ACL-protected leftover must never discard a computed result.
            }
        }
    }
}
