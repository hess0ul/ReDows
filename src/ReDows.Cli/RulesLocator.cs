namespace ReDows.Cli;

/// <summary>
/// Resolves the default ruleset directory for a distributable single exe: the
/// working directory first (dev workflow), else the rules shipped next to the
/// executable. An explicit --rules value is never second-guessed.
/// </summary>
public static class RulesLocator
{
    public const string DefaultDirectory = "rules";

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
