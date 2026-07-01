using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ReDows.Core.Rules.Loading;

/// <summary>
/// Strict, fail-closed ruleset loader. YAML strictness: unknown keys and duplicate
/// keys are errors (a typo in a rule must block the scan, never be skipped).
/// Semantic lints cover what a schema cannot express: global id uniqueness,
/// token-vs-scope coherence, the bare-name policy (§0-8), layer constraints.
/// </summary>
public static partial class RulesetLoader
{
    public const int SupportedSchemaVersion = 1;

    [GeneratedRegex("^[a-z0-9]+([._-][a-z0-9]+)*$")]
    private static partial Regex IdPattern();

    public static Ruleset LoadDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            throw new RulesetValidationException(
                [new RulesetError(directory, null, "ruleset directory does not exist")]);
        }

        var paths = Directory.EnumerateFiles(directory, "*.yaml", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(directory, "*.yml", SearchOption.AllDirectories))
            // Win32 3-char-extension quirk: "*.yml" also matches ".ymlx" — a
            // stray file must never silently merge into the ruleset.
            .Where(f => f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
                || f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var readErrors = new List<RulesetError>();
        var files = new List<(string Path, string Content)>();
        foreach (var path in paths)
        {
            var relative = Path.GetRelativePath(directory, path);
            try
            {
                files.Add((relative, File.ReadAllText(path)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                readErrors.Add(new RulesetError(relative, null,
                    $"file unreadable ({ex.GetType().Name}: {ex.Message}) — refusing to load a partial ruleset (fail-closed)"));
            }
        }

        if (readErrors.Count > 0)
        {
            throw new RulesetValidationException(readErrors);
        }

        if (files.Count == 0)
        {
            throw new RulesetValidationException(
                [new RulesetError(directory, null, "no .yaml/.yml ruleset files found")]);
        }

        return LoadFiles(files);
    }

    public static Ruleset LoadFiles(IEnumerable<(string Path, string Content)> files)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .WithDuplicateKeyChecking()
            .Build();

        var errors = new List<RulesetError>();
        var rules = new List<Rule>();
        var seenIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var parsed = new List<(string Path, RulesetFileDto Dto)>();
        var templates = new Dictionary<string, (string File, TemplateDto Dto)>(StringComparer.Ordinal);

        // Pass 1: parse, version-check, register every template — apps may then
        // reference templates from any file (load order stays irrelevant).
        foreach (var (path, content) in files)
        {
            RulesetFileDto? dto;
            try
            {
                dto = deserializer.Deserialize<RulesetFileDto>(content);
            }
            catch (YamlException ex)
            {
                errors.Add(new RulesetError(path, null, $"YAML error: {Flatten(ex)}"));
                continue;
            }

            if (dto is null)
            {
                errors.Add(new RulesetError(path, null, "file is empty"));
                continue;
            }

            if (dto.SchemaVersion is null)
            {
                errors.Add(new RulesetError(path, null, "schema_version is required"));
                continue;
            }

            if (dto.SchemaVersion != SupportedSchemaVersion)
            {
                errors.Add(new RulesetError(path, null,
                    $"schema_version {dto.SchemaVersion} is not supported by this engine (max {SupportedSchemaVersion}) — refusing to load (fail-closed)"));
                continue;
            }

            foreach (var template in dto.Templates ?? [])
            {
                RegisterTemplate(path, template, templates, errors);
            }

            parsed.Add((path, dto));
        }

        // Pass 2: plain rules, then app instantiations.
        foreach (var (path, dto) in parsed)
        {
            BuildFile(path, dto, templates, rules, seenIds, errors);
        }

        if (errors.Count > 0)
        {
            throw new RulesetValidationException(errors);
        }

        return new Ruleset(SupportedSchemaVersion, rules);
    }

    private static void BuildFile(
        string file, RulesetFileDto dto,
        Dictionary<string, (string File, TemplateDto Dto)> templates,
        List<Rule> rules, Dictionary<string, string> seenIds, List<RulesetError> errors)
    {
        var hasRules = dto.Rules is { Count: > 0 };

        if (dto.Rules is null && dto.Templates is null && dto.Apps is null)
        {
            errors.Add(new RulesetError(file, null, "file declares no rules, templates or apps"));
            return;
        }

        if (hasRules)
        {
            if (!RuleVocabulary.TryParseLayer(dto.Layer, out var layer))
            {
                errors.Add(new RulesetError(file, null,
                    $"layer '{dto.Layer}' is invalid or missing (required with 'rules': carve_out, deny or capture)"));
            }
            else
            {
                foreach (var ruleDto in dto.Rules!)
                {
                    var rule = BuildRule(file, layer, ruleDto, seenIds, errors);
                    if (rule is not null)
                    {
                        rules.Add(rule);
                    }
                }
            }
        }
        else if (dto.Rules is { Count: 0 })
        {
            errors.Add(new RulesetError(file, null, "rules must contain at least one rule"));
        }
        else if (dto.Layer is not null)
        {
            errors.Add(new RulesetError(file, null,
                "layer is only meaningful for files with 'rules' (template rules carry their own layer)"));
        }

        foreach (var app in dto.Apps ?? [])
        {
            ExpandApp(file, app, templates, rules, seenIds, errors);
        }
    }

    [GeneratedRegex(@"\{([a-z0-9_]+)\}")]
    private static partial Regex PlaceholderPattern();

    private static void RegisterTemplate(
        string file, TemplateDto dto,
        Dictionary<string, (string File, TemplateDto Dto)> templates, List<RulesetError> errors)
    {
        if (string.IsNullOrWhiteSpace(dto.Name) || !IdPattern().IsMatch(dto.Name))
        {
            errors.Add(new RulesetError(file, dto.Name, "template name is required (lowercase id syntax)"));
            return;
        }

        if (!templates.TryAdd(dto.Name, (file, dto)))
        {
            errors.Add(new RulesetError(file, dto.Name,
                $"duplicate template name (already defined in {templates[dto.Name].File})"));
            return;
        }

        if (dto.Rules is null || dto.Rules.Count == 0)
        {
            errors.Add(new RulesetError(file, dto.Name, "template must declare at least one rule"));
            return;
        }

        // Fail-closed coherence: every placeholder used must be a declared param.
        // The scan is case-insensitive on purpose — '{Vendor_Path}' would neither
        // be flagged nor substituted (a silently dead rule), so it is an error.
        var declared = new HashSet<string>(dto.Params ?? [], StringComparer.Ordinal);
        foreach (var match in EnumerateTemplateMatches(dto.Rules))
        {
            foreach (System.Text.RegularExpressions.Match placeholder in AnyCasePlaceholderPattern().Matches(match ?? ""))
            {
                var name = placeholder.Groups[1].Value;
                if (name.Any(char.IsUpper))
                {
                    errors.Add(new RulesetError(file, dto.Name,
                        $"placeholder '{{{name}}}' must be all-lowercase (placeholders are case-sensitive — this one would never be substituted)"));
                }
                else if (!declared.Contains(name))
                {
                    errors.Add(new RulesetError(file, dto.Name,
                        $"placeholder '{{{name}}}' is not a declared param"));
                }
            }
        }
    }

    [GeneratedRegex(@"\{([A-Za-z0-9_]+)\}")]
    private static partial Regex AnyCasePlaceholderPattern();

    private static IEnumerable<string?> EnumerateTemplateMatches(List<TemplateRuleDto> rules)
    {
        foreach (var rule in rules)
        {
            yield return rule.Match;
            foreach (var match in EnumerateExceptionMatches(rule.Exceptions))
            {
                yield return match;
            }
        }
    }

    private static IEnumerable<string?> EnumerateExceptionMatches(List<TemplateExceptionDto>? exceptions)
    {
        foreach (var exception in exceptions ?? [])
        {
            yield return exception.Match;
            foreach (var match in EnumerateExceptionMatches(exception.Exceptions))
            {
                yield return match;
            }
        }
    }

    private static void ExpandApp(
        string file, AppDto app,
        Dictionary<string, (string File, TemplateDto Dto)> templates,
        List<Rule> rules, Dictionary<string, string> seenIds, List<RulesetError> errors)
    {
        if (string.IsNullOrWhiteSpace(app.Name) || !IdPattern().IsMatch(app.Name))
        {
            errors.Add(new RulesetError(file, app.Name, "app name is required (lowercase id syntax)"));
            return;
        }

        if (app.Template is null || !templates.TryGetValue(app.Template, out var template))
        {
            errors.Add(new RulesetError(file, app.Name, $"unknown template '{app.Template}'"));
            return;
        }

        var declared = template.Dto.Params ?? [];
        var provided = app.Params ?? new Dictionary<string, string>();
        var reported = errors.Count;
        foreach (var param in declared)
        {
            if (!provided.ContainsKey(param))
            {
                errors.Add(new RulesetError(file, app.Name, $"missing param '{param}' required by template '{app.Template}'"));
            }
        }

        foreach (var (key, value) in provided)
        {
            if (!declared.Contains(key, StringComparer.Ordinal))
            {
                errors.Add(new RulesetError(file, app.Name, $"param '{key}' is not declared by template '{app.Template}'"));
            }

            // Params are literal path fragments: a glob metacharacter would
            // silently widen the zone (an ignore-verdict one would lose data),
            // and braces would re-open placeholder substitution.
            if (value.AsSpan().IndexOfAny('*', '?', '{') >= 0 || value.Contains('}'))
            {
                errors.Add(new RulesetError(file, app.Name,
                    $"param '{key}' value '{value}' contains '*', '?', '{{' or '}}' — param values must be literal path fragments"));
            }
        }

        if (errors.Count > reported || template.Dto.Rules is null)
        {
            return;
        }

        foreach (var templateRule in template.Dto.Rules)
        {
            if (!RuleVocabulary.TryParseLayer(templateRule.Layer, out var layer))
            {
                errors.Add(new RulesetError(template.File, $"app.{app.Name}.{templateRule.IdSuffix}",
                    $"template rule layer '{templateRule.Layer}' is invalid (expected carve_out, deny or capture)"));
                continue;
            }

            var ruleDto = new RuleDto
            {
                Id = $"app.{app.Name}.{templateRule.IdSuffix}",
                Scope = templateRule.Scope,
                Match = Substitute(templateRule.Match, provided),
                Verdict = templateRule.Verdict,
                Prio = templateRule.Prio,
                Flags = templateRule.Flags,
                Note = templateRule.Note,
                Source = app.Source ?? $"template:{app.Template}",
                Exceptions = ExpandExceptions(app.Name!, templateRule.Exceptions, provided),
            };

            var rule = BuildRule(file, layer, ruleDto, seenIds, errors);
            if (rule is not null)
            {
                rules.Add(rule);
            }
        }
    }

    private static List<ExceptionDto>? ExpandExceptions(
        string appName, List<TemplateExceptionDto>? exceptions, Dictionary<string, string> values)
    {
        if (exceptions is null)
        {
            return null;
        }

        return exceptions.Select(exception => new ExceptionDto
        {
            Id = $"app.{appName}.{exception.IdSuffix}",
            Match = Substitute(exception.Match, values),
            Verdict = exception.Verdict,
            Prio = exception.Prio,
            Flags = exception.Flags,
            Note = exception.Note,
            Exceptions = ExpandExceptions(appName, exception.Exceptions, values),
        }).ToList();
    }

    private static string? Substitute(string? text, IReadOnlyDictionary<string, string> values)
    {
        if (text is null)
        {
            return null;
        }

        // Single regex pass: substituted values are never rescanned, so the
        // result cannot depend on dictionary enumeration order (determinism).
        return PlaceholderPattern().Replace(text, match =>
            values.TryGetValue(match.Groups[1].Value, out var value) ? value : match.Value);
    }

    private static Rule? BuildRule(
        string file, RuleLayer layer, RuleDto dto,
        Dictionary<string, string> seenIds, List<RulesetError> errors)
    {
        var id = ValidateId(file, dto.Id, seenIds, errors);
        var reported = errors.Count;

        if (!RuleVocabulary.TryParseScope(dto.Scope, out var scope))
        {
            errors.Add(new RulesetError(file, id, $"scope '{dto.Scope}' is invalid (expected machine, user, volume or drive)"));
        }

        if (!RuleVocabulary.TryParseVerdict(dto.Verdict, out var verdict))
        {
            errors.Add(new RulesetError(file, id, $"verdict '{dto.Verdict}' is invalid"));
        }

        var priority = ParsePriority(file, id, dto.Prio, errors);
        var when = BuildCondition(file, id, dto.When, errors);

        PathPattern? pattern = null;
        if (errors.Count == reported)
        {
            pattern = PathPattern.Parse(dto.Match, scope, out var patternError);
            if (pattern is null)
            {
                errors.Add(new RulesetError(file, id, patternError!));
            }
        }

        BareNameClass? bareNameClass = null;
        if (dto.BareNameClass is not null)
        {
            if (RuleVocabulary.TryParseBareNameClass(dto.BareNameClass, out var parsed))
            {
                bareNameClass = parsed;
            }
            else
            {
                errors.Add(new RulesetError(file, id, $"bare_name_class '{dto.BareNameClass}' is invalid (expected distinctive or collision_prone)"));
            }
        }

        if (errors.Count == reported)
        {
            // Bare-name policy (deny-list §0-8): a floating ignore must declare its
            // collision class; collision-prone names must prove their context.
            var isFloatingIgnore = scope == RuleScope.Drive && verdict == Verdict.Ignore;
            if (isFloatingIgnore && bareNameClass is null)
            {
                errors.Add(new RulesetError(file, id,
                    "floating ignore rules (scope drive + verdict ignore) must declare bare_name_class (deny-list §0-8)"));
            }
            else if (!isFloatingIgnore && bareNameClass is not null)
            {
                errors.Add(new RulesetError(file, id,
                    "bare_name_class only applies to floating ignore rules (scope drive + verdict ignore) — remove it"));
            }
            else if (bareNameClass == BareNameClass.CollisionProne && when is null)
            {
                errors.Add(new RulesetError(file, id,
                    "collision_prone bare-name rules must carry a 'when' context condition (deny-list §0-8)"));
            }

            if (layer == RuleLayer.CarveOut && verdict == Verdict.Ignore)
            {
                errors.Add(new RulesetError(file, id,
                    "carve_out rules protect zones from being ignored and must not have verdict 'ignore' (deny-list §C)"));
            }
        }

        var exceptions = BuildExceptions(file, id, scope, dto.Exceptions, seenIds, errors);
        var flags = ParseFlags(file, id, dto.Flags, errors);

        if (errors.Count > reported || id is null || pattern is null)
        {
            return null;
        }

        return new Rule(id, layer, scope, pattern, verdict, priority, when, bareNameClass,
            exceptions, dto.Source, dto.Note, flags);
    }

    private static IReadOnlyList<RuleFlag>? ParseFlags(
        string file, string? ruleId, List<string>? texts, List<RulesetError> errors)
    {
        if (texts is null || texts.Count == 0)
        {
            return null;
        }

        var flags = new List<RuleFlag>();
        foreach (var text in texts)
        {
            if (RuleVocabulary.TryParseFlag(text, out var flag))
            {
                flags.Add(flag);
            }
            else
            {
                errors.Add(new RulesetError(file, ruleId, $"flag '{text}' is invalid (expected dpapi_machine_bound)"));
            }
        }

        return flags;
    }

    private static IReadOnlyList<RuleException> BuildExceptions(
        string file, string? parentId, RuleScope scope, List<ExceptionDto>? dtos,
        Dictionary<string, string> seenIds, List<RulesetError> errors)
    {
        if (dtos is null || dtos.Count == 0)
        {
            return [];
        }

        var result = new List<RuleException>();
        foreach (var dto in dtos)
        {
            var id = ValidateId(file, dto.Id, seenIds, errors);
            var reported = errors.Count;

            if (!RuleVocabulary.TryParseVerdict(dto.Verdict, out var verdict))
            {
                errors.Add(new RulesetError(file, id ?? parentId, $"verdict '{dto.Verdict}' is invalid"));
            }

            var priority = ParsePriority(file, id ?? parentId, dto.Prio, errors);
            var when = BuildCondition(file, id ?? parentId, dto.When, errors);

            var pattern = PathPattern.Parse(dto.Match, scope, out var patternError);
            if (pattern is null)
            {
                errors.Add(new RulesetError(file, id ?? parentId, patternError!));
            }

            var nested = BuildExceptions(file, id ?? parentId, scope, dto.Exceptions, seenIds, errors);
            var flags = ParseFlags(file, id ?? parentId, dto.Flags, errors);

            if (errors.Count == reported && id is not null && pattern is not null)
            {
                result.Add(new RuleException(id, pattern, verdict, priority, when, nested, dto.Source, dto.Note, flags));
            }
        }

        return result;
    }

    private static RuleCondition? BuildCondition(
        string file, string? ruleId, ConditionDto? dto, List<RulesetError> errors)
    {
        if (dto is null)
        {
            return null;
        }

        var set = new List<string>();
        if (dto.All is not null)
        {
            set.Add("all");
        }

        if (dto.Any is not null)
        {
            set.Add("any");
        }

        if (dto.None is not null)
        {
            set.Add("none");
        }

        if (dto.AncestorMarker is not null)
        {
            set.Add("ancestor_marker");
        }

        if (set.Count != 1)
        {
            errors.Add(new RulesetError(file, ruleId,
                $"a condition must contain exactly one of all/any/none/ancestor_marker (found: {(set.Count == 0 ? "none" : string.Join(", ", set))})"));
            return null;
        }

        if (dto.AncestorMarker is not null)
        {
            if (dto.AncestorMarker.Count == 0)
            {
                errors.Add(new RulesetError(file, ruleId, "ancestor_marker must list at least one marker glob"));
                return null;
            }

            foreach (var marker in dto.AncestorMarker)
            {
                if (string.IsNullOrWhiteSpace(marker) || marker.Contains('/') || marker.Contains('\\'))
                {
                    errors.Add(new RulesetError(file, ruleId,
                        $"ancestor_marker entries must be single-segment name globs (got '{marker}')"));
                    return null;
                }
            }

            return new AncestorMarkerCondition(dto.AncestorMarker);
        }

        var children = dto.All ?? dto.Any ?? dto.None!;
        if (children.Count == 0)
        {
            errors.Add(new RulesetError(file, ruleId, $"{set[0]} must list at least one condition"));
            return null;
        }

        var built = new List<RuleCondition>();
        foreach (var child in children)
        {
            var condition = BuildCondition(file, ruleId, child, errors);
            if (condition is null)
            {
                return null;
            }

            built.Add(condition);
        }

        if (dto.All is not null)
        {
            return new AllOfCondition(built);
        }

        return dto.Any is not null ? new AnyOfCondition(built) : new NoneOfCondition(built);
    }

    private static string? ValidateId(
        string file, string? id, Dictionary<string, string> seenIds, List<RulesetError> errors)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            errors.Add(new RulesetError(file, null, "id is required"));
            return null;
        }

        if (!IdPattern().IsMatch(id))
        {
            errors.Add(new RulesetError(file, id,
                "id must be lowercase alphanumeric segments separated by '.', '_' or '-'"));
            return null;
        }

        // Engine-reserved ids (rules/README.md): a ruleset rule with such an id
        // would merge with engine items in the report and corrupt attribution.
        if (id == Classification.Classification.DefaultRuleId
            || id == "engine"
            || id.StartsWith("engine.", StringComparison.Ordinal))
        {
            errors.Add(new RulesetError(file, id,
                "id is reserved for the engine (engine.* and default.review — see rules/README.md)"));
            return null;
        }

        if (!seenIds.TryAdd(id, file))
        {
            errors.Add(new RulesetError(file, id,
                $"duplicate id (already defined in {seenIds[id]}) — one zone, one verdict (deny-list §0-9)"));
            return null;
        }

        return id;
    }

    private static RulePriority ParsePriority(
        string file, string? ruleId, string? text, List<RulesetError> errors)
    {
        if (text is null)
        {
            return RulePriority.Normal;
        }

        if (!RuleVocabulary.TryParsePriority(text, out var priority))
        {
            errors.Add(new RulesetError(file, ruleId,
                $"prio '{text}' is invalid (expected critical, high, normal or low)"));
            return RulePriority.Normal;
        }

        return priority;
    }

    private static string Flatten(YamlException ex)
    {
        var message = $"{ex.Message} (at {ex.Start})";
        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        {
            message += $" — {inner.Message}";
        }

        return message;
    }
}
