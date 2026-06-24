namespace ReDows.Core.Secrets;

/// <summary>What the reader saw under one subkey — value NAMES only, never values.</summary>
public sealed record SubkeyObservation(string Name, IReadOnlyList<string> ValueNames);

/// <summary>
/// A names-only snapshot of a target's key. It deliberately carries NO value bytes:
/// the pure evaluator literally cannot receive a secret value, so "location, never the
/// value" is enforced by the type, not by discipline (invariant: secrets kept apart).
/// </summary>
public sealed record RegistryObservation(
    bool Present,
    string? Error,
    IReadOnlyList<string> ValueNames,
    IReadOnlyList<SubkeyObservation> Subkeys)
{
    /// <summary>The key does not exist (the app stored nothing here).</summary>
    public static RegistryObservation Missing { get; } = new(Present: false, Error: null, [], []);

    /// <summary>The key could not be read (e.g. requires elevation): counted, never guessed.</summary>
    public static RegistryObservation Unreadable(string error) => new(Present: false, error, [], []);
}

/// <summary>What one located value name is: a known secret, known config, or an unknown name (review).</summary>
public enum SecretClass
{
    Secret,
    Config,
    Review,
}

/// <summary>One located value name under a target — its full registry path and class. Never a value.</summary>
public sealed record RegistryItemFinding(string Location, string ValueName, SecretClass Class);

/// <summary>Outcome of inspecting a target on a machine.</summary>
public enum TargetStatus
{
    PresentWithSecret,
    PresentNoSecret,
    Absent,
    Unreadable,
}

/// <summary>The result of probing one <see cref="RegistrySecretTarget"/>: status plus the located names.</summary>
public sealed record RegistrySecretFinding(
    RegistrySecretTarget Target,
    TargetStatus Status,
    string? Error,
    IReadOnlyList<RegistryItemFinding> Items);

/// <summary>
/// Pure classification of a names-only <see cref="RegistryObservation"/> against a
/// <see cref="RegistrySecretTarget"/>. No registry access, no values — testable on
/// fixtures. A value name in the target's secret set is a Secret; in its config set,
/// Config; otherwise Review (default-to-review: an unknown name in a secrets zone is
/// surfaced, never silently dropped).
/// </summary>
public static class RegistrySecretProbe
{
    public static RegistrySecretFinding Evaluate(RegistrySecretTarget target, RegistryObservation observation)
    {
        if (observation.Error is not null)
        {
            return new RegistrySecretFinding(target, TargetStatus.Unreadable, observation.Error, []);
        }

        if (!observation.Present)
        {
            return new RegistrySecretFinding(target, TargetStatus.Absent, Error: null, []);
        }

        var items = new List<RegistryItemFinding>();
        var prefix = Prefix(target);
        if (target.Shape == EntryShape.Values)
        {
            foreach (var name in observation.ValueNames)
            {
                items.Add(Classify(target, prefix, name));
            }
        }
        else
        {
            foreach (var subkey in observation.Subkeys)
            {
                foreach (var name in subkey.ValueNames)
                {
                    items.Add(Classify(target, $"{prefix}\\{subkey.Name}", name));
                }
            }
        }

        var status = items.Any(i => i.Class == SecretClass.Secret)
            ? TargetStatus.PresentWithSecret
            : TargetStatus.PresentNoSecret;
        return new RegistrySecretFinding(target, status, Error: null, items);
    }

    private static RegistryItemFinding Classify(RegistrySecretTarget target, string location, string name)
    {
        var secretClass = target.SecretValueNames.Contains(name) ? SecretClass.Secret
            : target.ConfigValueNames.Contains(name) ? SecretClass.Config
            : target.Unmatched; // Config for host-key/MRU targets where any name is legit config; else Review (default).
        return new RegistryItemFinding($"{location}\\{name}", name, secretClass);
    }

    private static string Prefix(RegistrySecretTarget target) =>
        $"{(target.Hive == SecretHive.Hklm ? "HKLM" : "HKCU")}\\{target.KeyPath}";
}
