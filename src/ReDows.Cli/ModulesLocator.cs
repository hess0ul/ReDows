namespace ReDows.Cli;

/// <summary>
/// Resolves the category-module directory (modules/), mirroring <see cref="RulesLocator"/>:
/// the working directory first (dev workflow), else the modules shipped next to the
/// executable (distribution). A missing directory is NOT an error — the loader treats
/// it as "no modules" — so, unlike the ruleset, a resolved-but-absent path is fine.
/// </summary>
public static class ModulesLocator
{
    public const string DefaultDirectory = "modules";

    public static string Resolve(string requested)
    {
        if (requested != DefaultDirectory || Directory.Exists(requested))
        {
            return requested;
        }

        var nextToExe = Path.Combine(AppContext.BaseDirectory, DefaultDirectory);
        return Directory.Exists(nextToExe) ? nextToExe : requested;
    }
}
