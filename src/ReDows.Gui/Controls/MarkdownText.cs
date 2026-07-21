using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace ReDows.Gui.Controls;

/// <summary>
/// A tiny, dependency-free Markdown renderer for a <see cref="TextBlock"/>: bind
/// <c>controls:MarkdownText.Text</c> and headings (#), bold (**), inline code (`) and bullet/numbered
/// lists render as formatted inlines, so a chat model's Markdown answer stops leaking its raw #/**/`
/// markers into the UI. Not a full parser: it covers what a plain model reply uses, nothing more.
/// </summary>
public static class MarkdownText
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(MarkdownText), new PropertyMetadata("", OnTextChanged));

    public static void SetText(DependencyObject target, string value) => target.SetValue(TextProperty, value);

    public static string GetText(DependencyObject target) => (string)target.GetValue(TextProperty);

    private static void OnTextChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not TextBlock block)
        {
            return;
        }

        block.Inlines.Clear();
        Render(block, e.NewValue as string ?? "");
    }

    private static void Render(TextBlock block, string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var first = true;
        foreach (var line in lines)
        {
            if (!first)
            {
                block.Inlines.Add(new LineBreak());
            }

            first = false;

            var trimmed = line.TrimStart();
            var hashes = 0;
            while (hashes < trimmed.Length && trimmed[hashes] == '#')
            {
                hashes++;
            }

            // Heading: "# " ... "###### " → bold, a little larger; the markers themselves are dropped.
            if (hashes is > 0 and <= 6 && hashes < trimmed.Length && trimmed[hashes] == ' ')
            {
                var text = trimmed[(hashes + 1)..].Trim().Replace("**", "").Replace("`", "");
                block.Inlines.Add(new Run(text)
                {
                    FontWeight = FontWeights.SemiBold,
                    FontSize = block.FontSize + (hashes <= 2 ? 3 : 1),
                });
                continue;
            }

            // Bullet: "- " / "* " → "• ", keeping any indent so nested bullets stay nested.
            if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal))
            {
                var indent = line.Length - trimmed.Length;
                block.Inlines.Add(new Run(new string(' ', indent) + "•  "));
                AddInline(block, trimmed[2..]);
                continue;
            }

            // Anything else (including "1." numbered lists, whose prefix we keep): inline-format only.
            AddInline(block, line);
        }
    }

    /// <summary>Add one line's inlines, turning **bold** into bold runs and `code` into a monospace run.</summary>
    private static void AddInline(TextBlock block, string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            if (i + 1 < text.Length && text[i] == '*' && text[i + 1] == '*')
            {
                var end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end > 0)
                {
                    block.Inlines.Add(new Run(text[(i + 2)..end]) { FontWeight = FontWeights.SemiBold });
                    i = end + 2;
                    continue;
                }
            }

            if (text[i] == '`')
            {
                var end = text.IndexOf('`', i + 1);
                if (end > 0)
                {
                    block.Inlines.Add(new Run(text[(i + 1)..end]) { FontFamily = new FontFamily("Consolas") });
                    i = end + 1;
                    continue;
                }
            }

            var next = NextMarker(text, i + 1); // +1 so we always make progress past the current char
            block.Inlines.Add(new Run(text[i..next]));
            i = next;
        }
    }

    private static int NextMarker(string text, int from)
    {
        for (var j = from; j < text.Length; j++)
        {
            if (text[j] == '`' || (j + 1 < text.Length && text[j] == '*' && text[j + 1] == '*'))
            {
                return j;
            }
        }

        return text.Length;
    }
}
