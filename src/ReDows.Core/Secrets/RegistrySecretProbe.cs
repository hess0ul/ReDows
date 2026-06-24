namespace ReDows.Core.Secrets;

/// <summary>
/// One value seen under a key: its NAME, and its <see cref="Value"/> ONLY when the
/// reader classified the name as config and chose to read it. For a secret (or an
/// unknown name) <see cref="Value"/> is null — never read (invariant #5).
/// </summary>
public sealed record ObservedValue(string Name, string? Value = null);

/// <summary>What the reader saw under one subkey: value names, with config values only.</summary>
public sealed record SubkeyObservation(string Name, IReadOnlyList<ObservedValue> Values);

/// <summary>
/// A snapshot of a target's key. It may carry CONFIG values (host, port, user — safe
/// to keep) but NEVER a secret value: the reader reads a value only after classifying
/// its name as config, so a secret value is never read in the first place.
/// </summary>
public sealed record RegistryObservation(
    bool Present,
    string? Error,
    IReadOnlyList<ObservedValue> Values,
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

/// <summary>
/// One located value under a target: its full registry path, name, class, and — for a
/// config value only — its <see cref="Value"/>. A secret's value is always null here.
/// </summary>
public sealed record RegistryItemFinding(string Location, string ValueName, SecretClass Class, string? Value = null);

/// <summary>Outcome of inspecting a target on a machine.</summary>
public enum TargetStatus
{
    PresentWithSecret,
    PresentNoSecret,
    Absent,
    Unreadable,
}

/// <summary>The result of probing one <see cref="RegistrySecretTarget"/>: status plus the located items.</summary>
public sealed record RegistrySecretFinding(
    RegistrySecretTarget Target,
    TargetStatus Status,
    string? Error,
    IReadOnlyList<RegistryItemFinding> Items);

/// <summary>
/// Pure classification of a <see cref="RegistryObservation"/> against a target. A value
/// name in the target's secret set is a Secret; in its config set, Config; otherwise the
/// target's <see cref="RegistrySecretTarget.Unmatched"/> class (Config for host-key/MRU
/// targets, else Review — default-to-review). The reader calls <see cref="ClassifyName"/>
/// to decide whether to read a value at all; <see cref="Evaluate"/> is the second guard —
/// it surfaces a value ONLY for a config item, dropping any value on a non-config name.
/// </summary>
public static class RegistrySecretProbe
{
    public static SecretClass ClassifyName(RegistrySecretTarget target, string name) =>
        target.SecretValueNames.Contains(name) ? SecretClass.Secret
        : target.ConfigValueNames.Contains(name) ? SecretClass.Config
        : target.Unmatched;

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
            foreach (var value in observation.Values)
            {
                items.Add(Classify(target, prefix, value));
            }
        }
        else
        {
            foreach (var subkey in observation.Subkeys)
            {
                foreach (var value in subkey.Values)
                {
                    items.Add(Classify(target, $"{prefix}\\{subkey.Name}", value));
                }
            }
        }

        var status = items.Any(i => i.Class == SecretClass.Secret)
            ? TargetStatus.PresentWithSecret
            : TargetStatus.PresentNoSecret;
        return new RegistrySecretFinding(target, status, Error: null, items);
    }

    private static RegistryItemFinding Classify(RegistrySecretTarget target, string location, ObservedValue observed)
    {
        var secretClass = ClassifyName(target, observed.Name);
        // Second guard (defence in depth): only a CONFIG value is ever surfaced. A value
        // present on a secret/review name (a reader bug) is dropped and never output.
        var value = secretClass == SecretClass.Config ? observed.Value : null;
        return new RegistryItemFinding($"{location}\\{observed.Name}", observed.Name, secretClass, value);
    }

    private static string Prefix(RegistrySecretTarget target) =>
        $"{(target.Hive == SecretHive.Hklm ? "HKLM" : "HKCU")}\\{target.KeyPath}";
}
