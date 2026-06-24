namespace ReDows.Core.Apps;

/// <summary>
/// Minimal parser for Valve's text KeyValues format (VDF/ACF: libraryfolders.vdf,
/// appmanifest_*.acf): quoted keys, quoted string values or brace-nested blocks,
/// backslash escapes. Pure — testable on fixtures.
/// </summary>
public sealed class ValveKeyValues
{
    private readonly Dictionary<string, ValveKeyValues> _children = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, ValveKeyValues> Children => _children;

    public IReadOnlyDictionary<string, string> Values => _values;

    public string? GetValue(string key) => _values.GetValueOrDefault(key);

    public ValveKeyValues? GetChild(string key) => _children.GetValueOrDefault(key);

    /// <summary>
    /// Recursion guard: real VDF/ACF files nest 3-4 levels; a pathological file
    /// with thousands of '{' must degrade fail-soft, never overflow the stack.
    /// </summary>
    private const int MaxDepth = 64;

    /// <summary>Parses a document and returns its single root block's content.</summary>
    public static ValveKeyValues Parse(string text)
    {
        var position = 0;
        var root = new ValveKeyValues();
        while (TryReadToken(text, ref position, out var key))
        {
            if (!TryReadToken(text, ref position, out var value, out var isBlockOpen) || isBlockOpen)
            {
                root._children[key] = ParseBlock(text, ref position, depth: 1);
            }
            else
            {
                root._values[key] = value;
            }
        }

        // Conventional single-root documents ("libraryfolders" { … }) unwrap to their content.
        return root._values.Count == 0 && root._children.Count == 1
            ? root._children.Values.First()
            : root;
    }

    private static ValveKeyValues ParseBlock(string text, ref int position, int depth)
    {
        var node = new ValveKeyValues();
        if (depth > MaxDepth)
        {
            // Deeper than any real file: skip the whole block (balanced braces)
            // so the parent does not misread its content as its own (fail-soft).
            var nesting = 0;
            while (TryReadToken(text, ref position, out _, out var open, out var close))
            {
                if (open)
                {
                    nesting++;
                }
                else if (close && nesting-- == 0)
                {
                    break;
                }
            }

            return node;
        }

        while (true)
        {
            if (!TryReadToken(text, ref position, out var key, out var isBlockOpen, out var isBlockClose))
            {
                return node; // unterminated block: keep what was read (fail-soft, caller counts errors upstream)
            }

            if (isBlockClose)
            {
                return node;
            }

            if (isBlockOpen)
            {
                continue; // stray '{' — tolerate
            }

            if (!TryReadToken(text, ref position, out var value, out var valueIsOpen, out var valueIsClose))
            {
                return node;
            }

            if (valueIsClose)
            {
                return node;
            }

            if (valueIsOpen)
            {
                node._children[key] = ParseBlock(text, ref position, depth + 1);
            }
            else
            {
                node._values[key] = value;
            }
        }
    }

    private static bool TryReadToken(string text, ref int position, out string token) =>
        TryReadToken(text, ref position, out token, out _, out _);

    private static bool TryReadToken(string text, ref int position, out string token, out bool isBlockOpen) =>
        TryReadToken(text, ref position, out token, out isBlockOpen, out _);

    private static bool TryReadToken(
        string text, ref int position, out string token, out bool isBlockOpen, out bool isBlockClose)
    {
        token = "";
        isBlockOpen = false;
        isBlockClose = false;

        while (position < text.Length)
        {
            var c = text[position];
            if (char.IsWhiteSpace(c))
            {
                position++;
                continue;
            }

            if (c == '/' && position + 1 < text.Length && text[position + 1] == '/')
            {
                while (position < text.Length && text[position] != '\n')
                {
                    position++;
                }

                continue;
            }

            break;
        }

        if (position >= text.Length)
        {
            return false;
        }

        switch (text[position])
        {
            case '{':
                position++;
                isBlockOpen = true;
                return true;
            case '}':
                position++;
                isBlockClose = true;
                return true;
            case '"':
                position++;
                var builder = new System.Text.StringBuilder();
                while (position < text.Length && text[position] != '"')
                {
                    if (text[position] == '\\' && position + 1 < text.Length)
                    {
                        position++;
                        builder.Append(text[position] switch
                        {
                            'n' => '\n',
                            't' => '\t',
                            '"' => '"',
                            '\\' => '\\',
                            var other => other,
                        });
                    }
                    else
                    {
                        builder.Append(text[position]);
                    }

                    position++;
                }

                position++; // closing quote
                token = builder.ToString();
                return true;
            default:
                // Unquoted token (rare in these files): read to whitespace/brace.
                var start = position;
                while (position < text.Length && !char.IsWhiteSpace(text[position])
                    && text[position] != '{' && text[position] != '}')
                {
                    position++;
                }

                token = text[start..position];
                return true;
        }
    }
}
