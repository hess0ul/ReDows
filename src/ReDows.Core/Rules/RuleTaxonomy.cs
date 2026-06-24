namespace ReDows.Core.Rules;

/// <summary>
/// Final classification verdicts. There is no IGNORE_EXC verdict: an "ignore with
/// exceptions" is an ignore rule carrying nested <see cref="RuleException"/> entries,
/// which are evaluated before their parent.
/// </summary>
public enum Verdict
{
    Ignore,
    NoteOnly,
    Review,
    CaptureConfig,
    CaptureUser,
    CaptureSecret,
}

/// <summary>
/// Ruleset-declared evaluation stages. The engine evaluates fixed stages in order:
/// access errors → claimed zones (both engine-internal, reserved) → carve_out → deny
/// → capture → default REVIEW. The first stage with at least one matching rule decides.
/// </summary>
public enum RuleLayer
{
    CarveOut,
    Deny,
    Capture,
}

/// <summary>Where a rule pattern is anchored and instantiated.</summary>
public enum RuleScope
{
    /// <summary>Anchored at a machine location token (%SystemRoot%, %ProgramData%…).</summary>
    Machine,

    /// <summary>Instantiated once per user profile (ProfileList), anchored at a per-profile token.</summary>
    User,

    /// <summary>Anchored at the root of every volume.</summary>
    Volume,

    /// <summary>Floating: matched anywhere on every volume (pattern must start with **/).</summary>
    Drive,
}

public enum RulePriority
{
    Critical,
    High,
    Normal,
    Low,
}

/// <summary>
/// Orthogonal rule flags (an axis, never encoded in the verdict — bloc 1
/// decision). V1 ships one flag: DPAPI machine-bound data is capturable but
/// UNREADABLE after a reset, so capturing it without alerting would be a false
/// sense of safety — the report surfaces it as a pre-reset alert
/// ("export/synchronize BEFORE the reset"). Deny-list §D-14.
/// </summary>
public enum RuleFlag
{
    DpapiMachineBound,
}

/// <summary>
/// Self-declared collision class of a floating bare-name ignore pattern (deny-list §0-8).
/// Collision-prone names (build, dist, Cache…) must carry a context condition;
/// distinctive names (node_modules, __pycache__…) may stand alone.
/// </summary>
public enum BareNameClass
{
    Distinctive,
    CollisionProne,
}

/// <summary>String forms used in YAML, plus the normative conservativeness order.</summary>
public static class RuleVocabulary
{
    /// <summary>
    /// Normative conservativeness order (deny-list §0-7): when two matches tie on
    /// specificity, the verdict with the higher rank wins. Higher = safer against
    /// data loss. Over-capturing is a benign false positive; under-capturing is
    /// product failure.
    /// </summary>
    public static int ConservativenessRank(this Verdict verdict) => verdict switch
    {
        Verdict.Ignore => 0,
        Verdict.NoteOnly => 1,
        Verdict.Review => 2,
        Verdict.CaptureConfig => 3,
        Verdict.CaptureUser => 4,
        Verdict.CaptureSecret => 5,
        _ => throw new ArgumentOutOfRangeException(nameof(verdict)),
    };

    /// <summary>The three CAPTURE verdicts (things to keep): config, user data, secret.</summary>
    public static bool IsCapture(this Verdict verdict) =>
        verdict is Verdict.CaptureConfig or Verdict.CaptureUser or Verdict.CaptureSecret;

    public static bool TryParseVerdict(string? text, out Verdict verdict)
    {
        verdict = text switch
        {
            "ignore" => Verdict.Ignore,
            "note_only" => Verdict.NoteOnly,
            "review" => Verdict.Review,
            "capture:config" => Verdict.CaptureConfig,
            "capture:user" => Verdict.CaptureUser,
            "capture:secret" => Verdict.CaptureSecret,
            _ => (Verdict)(-1),
        };
        return (int)verdict >= 0;
    }

    public static string Format(this Verdict verdict) => verdict switch
    {
        Verdict.Ignore => "ignore",
        Verdict.NoteOnly => "note_only",
        Verdict.Review => "review",
        Verdict.CaptureConfig => "capture:config",
        Verdict.CaptureUser => "capture:user",
        Verdict.CaptureSecret => "capture:secret",
        _ => throw new ArgumentOutOfRangeException(nameof(verdict)),
    };

    public static bool TryParseLayer(string? text, out RuleLayer layer)
    {
        layer = text switch
        {
            "carve_out" => RuleLayer.CarveOut,
            "deny" => RuleLayer.Deny,
            "capture" => RuleLayer.Capture,
            _ => (RuleLayer)(-1),
        };
        return (int)layer >= 0;
    }

    public static string Format(this RuleLayer layer) => layer switch
    {
        RuleLayer.CarveOut => "carve_out",
        RuleLayer.Deny => "deny",
        RuleLayer.Capture => "capture",
        _ => throw new ArgumentOutOfRangeException(nameof(layer)),
    };

    public static bool TryParseScope(string? text, out RuleScope scope)
    {
        scope = text switch
        {
            "machine" => RuleScope.Machine,
            "user" => RuleScope.User,
            "volume" => RuleScope.Volume,
            "drive" => RuleScope.Drive,
            _ => (RuleScope)(-1),
        };
        return (int)scope >= 0;
    }

    public static bool TryParsePriority(string? text, out RulePriority priority)
    {
        priority = text switch
        {
            "critical" => RulePriority.Critical,
            "high" => RulePriority.High,
            "normal" => RulePriority.Normal,
            "low" => RulePriority.Low,
            _ => (RulePriority)(-1),
        };
        return (int)priority >= 0;
    }

    public static bool TryParseFlag(string? text, out RuleFlag flag)
    {
        flag = text switch
        {
            "dpapi_machine_bound" => RuleFlag.DpapiMachineBound,
            _ => (RuleFlag)(-1),
        };
        return (int)flag >= 0;
    }

    public static string Format(this RuleFlag flag) => flag switch
    {
        RuleFlag.DpapiMachineBound => "dpapi_machine_bound",
        _ => throw new ArgumentOutOfRangeException(nameof(flag)),
    };

    public static bool TryParseBareNameClass(string? text, out BareNameClass bareNameClass)
    {
        bareNameClass = text switch
        {
            "distinctive" => BareNameClass.Distinctive,
            "collision_prone" => BareNameClass.CollisionProne,
            _ => (BareNameClass)(-1),
        };
        return (int)bareNameClass >= 0;
    }
}
