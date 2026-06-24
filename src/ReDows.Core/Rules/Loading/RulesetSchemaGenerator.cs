using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReDows.Core.Rules.Loading;

/// <summary>
/// Generates rules/ruleset.schema.json from the loader DTOs by reflection — the C#
/// types and their attributes are the single source of truth, the schema is a build
/// artifact for editor support (yaml-language-server: autocompletion, hover docs,
/// live validation). Recursive DTOs (conditions, nested exceptions) are emitted as
/// $defs/$ref. A drift test keeps the committed file in sync.
/// </summary>
public static class RulesetSchemaGenerator
{
    private static readonly Type[] DefTypes =
    [
        typeof(RuleDto), typeof(ExceptionDto), typeof(ConditionDto),
        typeof(TemplateDto), typeof(TemplateRuleDto), typeof(TemplateExceptionDto), typeof(AppDto),
    ];

    public static string GenerateJson()
    {
        var schema = new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
        };

        foreach (var (key, value) in BuildObjectSchema(typeof(RulesetFileDto)).ToList())
        {
            schema[key] = value?.DeepClone();
        }

        var defs = new JsonObject();
        foreach (var type in DefTypes)
        {
            defs[DefName(type)] = BuildObjectSchema(type);
        }

        schema["$defs"] = defs;

        return schema.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    private static JsonObject BuildObjectSchema(Type type)
    {
        var schema = new JsonObject();
        if (GetStringArgument(type, "TitleAttribute") is { } title)
        {
            schema["title"] = title;
        }

        if (GetStringArgument(type, "DescriptionAttribute") is { } description)
        {
            schema["description"] = description;
        }

        schema["type"] = "object";

        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var name = ToSnakeCase(property.Name);
            properties[name] = BuildPropertySchema(property);
            if (HasAttribute(property, "RequiredAttribute"))
            {
                required.Add(name);
            }
        }

        schema["properties"] = properties;
        if (required.Count > 0)
        {
            schema["required"] = required;
        }

        // The strict loader rejects unknown keys; the editor must flag them too.
        schema["additionalProperties"] = false;
        return schema;
    }

    private static JsonObject BuildPropertySchema(PropertyInfo property)
    {
        var schema = BuildTypeSchema(property.PropertyType);
        if (GetStringArgument(property, "DescriptionAttribute") is { } description)
        {
            schema["description"] = description;
        }

        if (GetStringArgument(property, "PatternAttribute") is { } pattern)
        {
            schema["pattern"] = pattern;
        }

        return schema;
    }

    private static JsonObject BuildTypeSchema(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;

        if (type == typeof(int))
        {
            return new JsonObject { ["type"] = "integer" };
        }

        if (type == typeof(string))
        {
            return new JsonObject { ["type"] = "string" };
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
        {
            return new JsonObject
            {
                ["type"] = "array",
                ["items"] = BuildTypeSchema(type.GetGenericArguments()[0]),
            };
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>)
            && type.GetGenericArguments()[0] == typeof(string))
        {
            return new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = BuildTypeSchema(type.GetGenericArguments()[1]),
            };
        }

        if (DefTypes.Contains(type))
        {
            return new JsonObject { ["$ref"] = "#/$defs/" + DefName(type) };
        }

        throw new NotSupportedException($"unsupported DTO property type '{type}'");
    }

    private static string DefName(Type type) => type.Name.EndsWith("Dto", StringComparison.Ordinal)
        ? type.Name[..^3]
        : type.Name;

    // Attributes are read structurally (constructor argument of the matching
    // attribute name), so the generator does not depend on a specific library API.
    private static string? GetStringArgument(MemberInfo member, string attributeName) =>
        member.CustomAttributes
            .FirstOrDefault(a => a.AttributeType.Name == attributeName)
            ?.ConstructorArguments.FirstOrDefault().Value as string;

    private static bool HasAttribute(MemberInfo member, string attributeName) =>
        member.CustomAttributes.Any(a => a.AttributeType.Name == attributeName);

    private static string ToSnakeCase(string name)
    {
        var builder = new StringBuilder(name.Length + 4);
        foreach (var ch in name)
        {
            if (char.IsUpper(ch) && builder.Length > 0)
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(ch));
        }

        return builder.ToString();
    }
}
