using System.Text;
using System.Text.RegularExpressions;

namespace FarmingRpgMaker.App.Markdown;

/// <summary>
/// Small, forgiving Markdown parser for release notes: ATX headings, paragraphs,
/// bullet/numbered lists (nested by indent), fenced code, block quotes, rules, and
/// inline <c>**bold**</c>, <c>*italic*</c>, <c>`code`</c>, <c>[links](url)</c> and
/// bare URLs. Raw HTML tags are dropped. Anything unrecognised is shown as text.
/// </summary>
public static partial class MarkdownParser
{
    public static IReadOnlyList<MarkdownBlock> Parse(string? markdown)
    {
        var blocks = new List<MarkdownBlock>();
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return blocks;
        }

        markdown = HtmlCommentRegex().Replace(markdown, "");
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var paragraph = new StringBuilder();

        void FlushParagraph()
        {
            if (paragraph.Length > 0)
            {
                blocks.Add(new MarkdownParagraph(ParseInlines(paragraph.ToString())));
                paragraph.Clear();
            }
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var line = raw.TrimEnd();
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                FlushParagraph();
                var fence = trimmed[..3];
                var code = new StringBuilder();
                for (i++; i < lines.Length && !lines[i].TrimStart().StartsWith(fence, StringComparison.Ordinal); i++)
                {
                    if (code.Length > 0)
                    {
                        code.Append('\n');
                    }

                    code.Append(lines[i].TrimEnd());
                }

                blocks.Add(new MarkdownCodeBlock(code.ToString()));
                continue;
            }

            if (trimmed.Length == 0)
            {
                FlushParagraph();
                continue;
            }

            if (HeadingRegex().Match(trimmed) is { Success: true } heading)
            {
                FlushParagraph();
                blocks.Add(new MarkdownHeading(heading.Groups[1].Length, ParseInlines(heading.Groups[2].Value.TrimEnd('#', ' '))));
                continue;
            }

            if (RuleRegex().IsMatch(trimmed))
            {
                FlushParagraph();
                blocks.Add(new MarkdownRule());
                continue;
            }

            var indent = line.Length - trimmed.Length;
            if (BulletRegex().Match(trimmed) is { Success: true } bullet)
            {
                FlushParagraph();
                blocks.Add(new MarkdownListItem("•", Depth(indent), ParseInlines(bullet.Groups[1].Value)));
                continue;
            }

            if (NumberedRegex().Match(trimmed) is { Success: true } numbered)
            {
                FlushParagraph();
                blocks.Add(new MarkdownListItem(numbered.Groups[1].Value + ".", Depth(indent), ParseInlines(numbered.Groups[2].Value)));
                continue;
            }

            if (trimmed.StartsWith('>'))
            {
                FlushParagraph();
                blocks.Add(new MarkdownQuote(ParseInlines(trimmed.TrimStart('>', ' '))));
                continue;
            }

            // Continuation of a list item (indented text right after it) joins the item.
            if (indent > 0 && paragraph.Length == 0 && blocks.Count > 0 && blocks[^1] is MarkdownListItem item && i > 0 && lines[i - 1].Trim().Length > 0)
            {
                blocks[^1] = item with { Inlines = [.. item.Inlines, new MarkdownInline(" "), .. ParseInlines(trimmed)] };
                continue;
            }

            if (paragraph.Length > 0)
            {
                paragraph.Append(raw.EndsWith("  ", StringComparison.Ordinal) ? '\n' : ' ');
            }

            paragraph.Append(trimmed);
        }

        FlushParagraph();
        return blocks;
    }

    public static IReadOnlyList<MarkdownInline> ParseInlines(string text)
    {
        var result = new List<MarkdownInline>();
        text = HtmlTagRegex().Replace(text, "");
        var plain = new StringBuilder();
        var bold = false;
        var italic = false;

        void Flush()
        {
            if (plain.Length > 0)
            {
                result.Add(new MarkdownInline(plain.ToString(), bold, italic));
                plain.Clear();
            }
        }

        for (var i = 0; i < text.Length;)
        {
            var c = text[i];

            if (c == '\\' && i + 1 < text.Length && IsEscapable(text[i + 1]))
            {
                plain.Append(text[i + 1]);
                i += 2;
                continue;
            }

            if (c == '`')
            {
                var end = text.IndexOf('`', i + 1);
                if (end > i)
                {
                    Flush();
                    result.Add(new MarkdownInline(text[(i + 1)..end], bold, italic, Code: true));
                    i = end + 1;
                    continue;
                }
            }

            if (c == '[' && LinkRegex().Match(text, i) is { Success: true } link && link.Index == i)
            {
                Flush();
                result.Add(new MarkdownInline(link.Groups[1].Value, bold, italic, LinkUrl: link.Groups[2].Value));
                i += link.Length;
                continue;
            }

            if ((c == 'h' || c == 'H') && BareUrlRegex().Match(text, i) is { Success: true } url && url.Index == i
                && (i == 0 || !char.IsLetterOrDigit(text[i - 1])))
            {
                Flush();
                var value = url.Value.TrimEnd('.', ',', ')', ';', ':');
                result.Add(new MarkdownInline(value, bold, italic, LinkUrl: value));
                i += value.Length;
                continue;
            }

            if ((c == '*' || c == '_') && i + 1 < text.Length && text[i + 1] == c)
            {
                Flush();
                bold = !bold;
                i += 2;
                continue;
            }

            if ((c == '*' || c == '_') && IsEmphasisDelimiter(text, i, italic))
            {
                Flush();
                italic = !italic;
                i++;
                continue;
            }

            plain.Append(c);
            i++;
        }

        Flush();
        return result;
    }

    private static int Depth(int indent) => Math.Min(3, indent / 2);

    private static bool IsEscapable(char c) => "\\`*_{}[]()#+-.!>|~".Contains(c, StringComparison.Ordinal);

    private static bool IsEmphasisDelimiter(string text, int i, bool closing)
    {
        // Opening: followed by non-space; closing: preceded by non-space. Avoids "a * b" and snake_case.
        if (closing)
        {
            return i > 0 && !char.IsWhiteSpace(text[i - 1]) && (i + 1 >= text.Length || !char.IsLetterOrDigit(text[i + 1]));
        }

        return i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1])
            && (i == 0 || !char.IsLetterOrDigit(text[i - 1]))
            && text.IndexOf(text[i], i + 1) > i + 1;
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^([-*_])(\s*\1){2,}$")]
    private static partial Regex RuleRegex();

    [GeneratedRegex(@"^[-*+]\s+(?:\[[ xX]\]\s+)?(.*)$")]
    private static partial Regex BulletRegex();

    [GeneratedRegex(@"^(\d{1,3})[.)]\s+(.*)$")]
    private static partial Regex NumberedRegex();

    [GeneratedRegex(@"\G\[([^\]]+)\]\(([^)\s]+)(?:\s+""[^""]*"")?\)")]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"\Ghttps?://[^\s<>()\[\]]+", RegexOptions.IgnoreCase)]
    private static partial Regex BareUrlRegex();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex HtmlCommentRegex();

    [GeneratedRegex(@"</?[a-zA-Z][^>]*>")]
    private static partial Regex HtmlTagRegex();
}
