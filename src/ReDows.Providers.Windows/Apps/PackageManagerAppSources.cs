using ReDows.Core.Apps;

namespace ReDows.Providers.Windows.Apps;

/// <summary>
/// Chocolatey packages: one folder per package under &lt;root&gt;\lib with its
/// .nuspec — exact reinstall ids. Roots: %ChocolateyInstall% (relocatable),
/// the default ProgramData location, and UniGetUI's embedded copy.
/// </summary>
public static class ChocolateyAppSource
{
    public sealed record Result(IReadOnlyList<AppEntry> Entries, SourceAccounting Accounting);

    public static Result Enumerate()
    {
        var roots = new List<(string Path, string? Note)>();
        var configured = Environment.GetEnvironmentVariable("ChocolateyInstall");
        roots.Add((
            string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "chocolatey")
                : configured,
            null));
        roots.Add((
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UniGetUI", "Chocolatey"),
            "via UniGetUI's embedded Chocolatey"));

        var entries = new List<AppEntry>();
        long enumerated = 0, apps = 0, errors = 0;

        foreach (var (root, note) in roots.DistinctBy(r => r.Path, StringComparer.OrdinalIgnoreCase))
        {
            var lib = Path.Combine(root, "lib");
            if (!Directory.Exists(lib))
            {
                continue;
            }

            foreach (var packageDirectory in Directory.EnumerateDirectories(lib))
            {
                enumerated++;
                try
                {
                    var packageName = Path.GetFileName(packageDirectory);
                    var nuspec = Path.Combine(packageDirectory, packageName + ".nuspec");
                    var package = File.Exists(nuspec) ? ChocolateyNuspec.Parse(File.ReadAllText(nuspec)) : null;
                    if (package is null)
                    {
                        errors++;
                        continue;
                    }

                    apps++;
                    entries.Add(new AppEntry(
                        Key: $"choco:{package.Id}",
                        Source: "choco",
                        Kind: AppEntryKind.App,
                        Name: package.Title ?? package.Id,
                        Version: package.Version,
                        Publisher: null,
                        InstallLocation: packageDirectory,
                        Scope: "machine",
                        Reinstall: new ReinstallHint("choco", package.Id, ReinstallConfidence.Exact),
                        Note: note));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
                {
                    errors++;
                }
            }
        }

        return new Result(entries, new SourceAccounting("choco", enumerated, apps, 0, 0, errors));
    }
}

/// <summary>
/// Scoop apps: &lt;root&gt;\apps\&lt;name&gt;\current\install.json (+ manifest.json
/// version). Roots: %SCOOP% / %SCOOP_GLOBAL% (relocatable) with their defaults.
/// </summary>
public static class ScoopAppSource
{
    public sealed record Result(IReadOnlyList<AppEntry> Entries, SourceAccounting Accounting);

    public static Result Enumerate()
    {
        var userRoot = Environment.GetEnvironmentVariable("SCOOP");
        var globalRoot = Environment.GetEnvironmentVariable("SCOOP_GLOBAL");
        var roots = new[]
        {
            (Path: string.IsNullOrWhiteSpace(userRoot)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop")
                : userRoot, Scope: "user"),
            (Path: string.IsNullOrWhiteSpace(globalRoot)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "scoop")
                : globalRoot, Scope: "machine"),
        };

        var entries = new List<AppEntry>();
        long enumerated = 0, apps = 0, errors = 0;

        foreach (var (root, scope) in roots.DistinctBy(r => r.Path, StringComparer.OrdinalIgnoreCase))
        {
            var appsDirectory = Path.Combine(root, "apps");
            if (!Directory.Exists(appsDirectory))
            {
                continue;
            }

            foreach (var appDirectory in Directory.EnumerateDirectories(appsDirectory))
            {
                enumerated++;
                try
                {
                    var name = Path.GetFileName(appDirectory);
                    var current = Path.Combine(appDirectory, "current");
                    var installJson = Path.Combine(current, "install.json");
                    var manifestJson = Path.Combine(current, "manifest.json");

                    var bucket = File.Exists(installJson) ? ScoopManifests.ParseBucket(File.ReadAllText(installJson)) : null;
                    var version = File.Exists(manifestJson) ? ScoopManifests.ParseVersion(File.ReadAllText(manifestJson)) : null;

                    apps++;
                    entries.Add(new AppEntry(
                        Key: $"scoop:{scope}:{name}",
                        Source: "scoop",
                        Kind: AppEntryKind.App,
                        Name: name,
                        Version: version,
                        Publisher: null,
                        InstallLocation: appDirectory,
                        Scope: scope,
                        Reinstall: bucket is null
                            ? new ReinstallHint("scoop", name, ReinstallConfidence.Candidate)
                            : new ReinstallHint("scoop", $"{bucket}/{name}", ReinstallConfidence.Exact),
                        Note: bucket is null ? "install.json has no bucket — id without bucket" : null));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                {
                    errors++;
                }
            }
        }

        return new Result(entries, new SourceAccounting("scoop", enumerated, apps, 0, 0, errors));
    }
}
