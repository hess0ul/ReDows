namespace ReDows.Core.Secrets;

/// <summary>
/// The result of a registry-secrets pass: one finding per catalog target, with a
/// closed accounting — every target is exactly one of present-with-secret /
/// present-without-secret / absent / unreadable (no target silently dropped).
/// </summary>
public sealed record RegistrySecretsReport(IReadOnlyList<RegistrySecretFinding> Findings)
{
    public int Total => Findings.Count;

    public int PresentWithSecret => Count(TargetStatus.PresentWithSecret);

    public int PresentNoSecret => Count(TargetStatus.PresentNoSecret);

    public int Absent => Count(TargetStatus.Absent);

    public int Unreadable => Count(TargetStatus.Unreadable);

    /// <summary>Targets that hold a secret here → export BEFORE the reset (with their hint and sensitivity).</summary>
    public IReadOnlyList<RegistrySecretFinding> PreResetAlerts =>
        Findings.Where(f => f.Status == TargetStatus.PresentWithSecret).ToList();

    private int Count(TargetStatus status) => Findings.Count(f => f.Status == status);
}

/// <summary>
/// Pure driver: classify every catalog target through a names-only observation source.
/// The <paramref name="observe"/> delegate supplies observations (the real provider on
/// a machine, or a fixture in a test), so the whole pass is testable without a registry.
/// </summary>
public static class RegistrySecretsScan
{
    public static RegistrySecretsReport Run(
        RegistrySecretsCatalog catalog, Func<RegistrySecretTarget, RegistryObservation> observe) =>
        new(catalog.Targets.Select(target => RegistrySecretProbe.Evaluate(target, ObserveSafely(target, observe))).ToList());

    /// <summary>
    /// The impurity boundary: <paramref name="observe"/> does live registry I/O. Any fault
    /// becomes a per-target Unreadable so a single bad/locked target can never abort the whole
    /// pass — total accounting holds against every exception class, not just today's known ones.
    /// </summary>
    private static RegistryObservation ObserveSafely(
        RegistrySecretTarget target, Func<RegistrySecretTarget, RegistryObservation> observe)
    {
        try
        {
            return observe(target);
        }
        catch (Exception ex)
        {
            return RegistryObservation.Unreadable(ex.GetType().Name);
        }
    }
}
