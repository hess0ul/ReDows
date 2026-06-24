using Json.Schema.Generation;

namespace ReDows.Core.Rules.Loading;

// YAML shape of a ruleset file (snake_case keys). These DTOs are the single
// source of truth for the generated JSON Schema (editor autocompletion and live
// validation); runtime enforcement is the strict loader + semantic lints.

[Title("ReDows ruleset file")]
[Description("One ruleset file: a schema version plus rules (with their layer), and/or app templates and their instantiations.")]
public sealed class RulesetFileDto
{
    [Required]
    [Description("Ruleset schema version understood by the engine. A file declaring a newer version is refused (fail-closed).")]
    public int? SchemaVersion { get; set; }

    [Pattern("^(carve_out|deny|capture)$")]
    [Description("Evaluation stage of every rule in this file (required when 'rules' is present). Stage order: carve_out → deny → capture; the first stage with a match decides.")]
    public string? Layer { get; set; }

    [Description("The classification rules of this file.")]
    public List<RuleDto>? Rules { get; set; }

    [Description("Per-app templates (apps-ctt catalogue): a shared pattern (P-CHROM…) declared once, instantiated by 'apps' entries. Template rules carry their own layer.")]
    public List<TemplateDto>? Templates { get; set; }

    [Description("Template instantiations: each app expands its template into ordinary rules with ids 'app.<name>.<suffix>'.")]
    public List<AppDto>? Apps { get; set; }
}

[Title("App template")]
public sealed class TemplateDto
{
    [Required]
    [Pattern("^[a-z0-9]+([._-][a-z0-9]+)*$")]
    [Description("Template name, referenced by apps.")]
    public string? Name { get; set; }

    [Description("Parameter names this template requires. Every '{param}' placeholder used in matches must be declared here.")]
    public List<string>? Params { get; set; }

    [Required]
    [Description("The rules this template expands to, one per app instantiation.")]
    public List<TemplateRuleDto>? Rules { get; set; }
}

[Title("Template rule")]
public sealed class TemplateRuleDto
{
    [Required]
    [Pattern("^[a-z0-9]+([._-][a-z0-9]+)*$")]
    [Description("Rule id becomes 'app.<app name>.<id_suffix>'.")]
    public string? IdSuffix { get; set; }

    [Required]
    [Pattern("^(carve_out|deny|capture)$")]
    [Description("Evaluation stage of this expanded rule (a template may mix stages: capture the profile, deny its caches).")]
    public string? Layer { get; set; }

    [Required]
    [Pattern("^(machine|user|volume|drive)$")]
    public string? Scope { get; set; }

    [Required]
    [Description("Bounded glob; '{param}' placeholders are substituted with the app's param values at expansion.")]
    public string? Match { get; set; }

    [Required]
    [Pattern("^(ignore|review|note_only|capture:(user|config|secret))$")]
    public string? Verdict { get; set; }

    [Pattern("^(critical|high|normal|low)$")]
    public string? Prio { get; set; }

    [Description("Orthogonal flags, same vocabulary as rule flags.")]
    public List<string>? Flags { get; set; }

    [Description("Nested exceptions; their ids are suffixed like rule ids.")]
    public List<TemplateExceptionDto>? Exceptions { get; set; }

    public string? Note { get; set; }
}

[Title("Template exception")]
public sealed class TemplateExceptionDto
{
    [Required]
    [Pattern("^[a-z0-9]+([._-][a-z0-9]+)*$")]
    public string? IdSuffix { get; set; }

    [Required]
    public string? Match { get; set; }

    [Required]
    [Pattern("^(ignore|review|note_only|capture:(user|config|secret))$")]
    public string? Verdict { get; set; }

    [Pattern("^(critical|high|normal|low)$")]
    public string? Prio { get; set; }

    [Description("Orthogonal flags, same vocabulary as rule flags.")]
    public List<string>? Flags { get; set; }

    public List<TemplateExceptionDto>? Exceptions { get; set; }

    public string? Note { get; set; }
}

[Title("App")]
[Description("One template instantiation: an application from the apps-ctt catalogue.")]
public sealed class AppDto
{
    [Required]
    [Pattern("^[a-z0-9]+([._-][a-z0-9]+)*$")]
    [Description("Application name, used in expanded rule ids ('app.<name>.<suffix>').")]
    public string? Name { get; set; }

    [Required]
    [Description("Name of the template to expand.")]
    public string? Template { get; set; }

    [Description("Values for the template's declared params (exactly the declared set).")]
    public Dictionary<string, string>? Params { get; set; }

    [Description("Provenance (catalogue section).")]
    public string? Source { get; set; }

    public string? Note { get; set; }
}

[Title("Rule")]
public sealed class RuleDto
{
    [Required]
    [Pattern("^[a-z0-9]+([._-][a-z0-9]+)*$")]
    [Description("Stable unique identifier, referenced by manifests and the review learning loop.")]
    public string? Id { get; set; }

    [Required]
    [Pattern("^(machine|user|volume|drive)$")]
    [Description("Anchoring: machine (machine token), user (instantiated per profile), volume (volume root), drive (floating, must start with **/).")]
    public string? Scope { get; set; }

    [Required]
    [Description("Bounded glob with '/' separators. First segment may be a location token: %SystemRoot%-style env token, FOLDERID_X Known Folder, or <UserProfile>. Absolute paths are forbidden.")]
    public string? Match { get; set; }

    [Required]
    [Pattern("^(ignore|review|note_only|capture:(user|config|secret))$")]
    [Description("Classification verdict. 'ignore with exceptions' is expressed with the exceptions list, not a separate verdict.")]
    public string? Verdict { get; set; }

    [Pattern("^(critical|high|normal|low)$")]
    [Description("Priority axis (orthogonal to the verdict). Default: normal.")]
    public string? Prio { get; set; }

    [Pattern("^(distinctive|collision_prone)$")]
    [Description("Required on floating bare-name ignore rules (scope drive + verdict ignore): collision_prone rules must carry a 'when' condition (deny-list §0-8).")]
    public string? BareNameClass { get; set; }

    [Description("Context condition gating the rule. When the condition is false the rule does not match.")]
    public ConditionDto? When { get; set; }

    [Description("Nested exceptions with their own verdict, evaluated before this rule (deny-list §0-9).")]
    public List<ExceptionDto>? Exceptions { get; set; }

    [Description("Provenance: where this rule comes from (design note section, external source).")]
    public string? Source { get; set; }

    [Description("Free-form rationale for human readers.")]
    public string? Note { get; set; }

    [Description("Orthogonal flags (an axis, never encoded in the verdict). dpapi_machine_bound: the captured bytes are DPAPI-bound to this machine and unreadable after a reset — surfaced as a pre-reset alert (export/synchronize BEFORE).")]
    public List<string>? Flags { get; set; }
}

[Title("Rule exception")]
public sealed class ExceptionDto
{
    [Required]
    [Pattern("^[a-z0-9]+([._-][a-z0-9]+)*$")]
    [Description("Stable unique identifier (same namespace as rule ids).")]
    public string? Id { get; set; }

    [Required]
    [Description("Bounded glob, same grammar and anchoring as the parent rule. Must target paths inside the parent's zone.")]
    public string? Match { get; set; }

    [Required]
    [Pattern("^(ignore|review|note_only|capture:(user|config|secret))$")]
    public string? Verdict { get; set; }

    [Pattern("^(critical|high|normal|low)$")]
    public string? Prio { get; set; }

    public ConditionDto? When { get; set; }

    [Description("Nested exceptions to this exception (recursive).")]
    public List<ExceptionDto>? Exceptions { get; set; }

    public string? Source { get; set; }

    public string? Note { get; set; }

    [Description("Orthogonal flags, same vocabulary as rule flags.")]
    public List<string>? Flags { get; set; }
}

[Title("Condition")]
[Description("Exactly one of: all, any, ancestor_marker.")]
public sealed class ConditionDto
{
    [Description("True when every child condition is true.")]
    public List<ConditionDto>? All { get; set; }

    [Description("True when at least one child condition is true.")]
    public List<ConditionDto>? Any { get; set; }

    [Description("True when an ancestor directory of the item (up to the volume root) contains an entry matching one of these name globs (e.g. package.json, *.sln, .git).")]
    public List<string>? AncestorMarker { get; set; }
}
