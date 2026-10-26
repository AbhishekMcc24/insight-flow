using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace InsightFlow.Web.Components.Skills;

/// <summary>
/// A small, safe Markdown to HTML converter for skill instructions: headings, ordered and bullet lists, quotes, code (fenced and inline),
/// bold, italics, links to http(s) addresses and paragraphs. Every piece of text is HTML-encoded first and only the tags written here are
/// produced, so the result can be shown as markup without a sanitizer. No images, no raw HTML.
/// </summary>
public static partial class SimpleMarkdown
{
    [GeneratedRegex(@"^(#{1,6})\s+(.*)$", RegexOptions.None, matchTimeoutMilliseconds: 500)]
    private static partial Regex Heading();

    [GeneratedRegex(@"^\s*\d+[.)]\s+(.*)$", RegexOptions.None, matchTimeoutMilliseconds: 500)]
    private static partial Regex Ordered();

    [GeneratedRegex(@"^\s*[-*+]\s+(.*)$", RegexOptions.None, matchTimeoutMilliseconds: 500)]
    private static partial Regex Bullet();

    [GeneratedRegex(@"`([^`]+)`", RegexOptions.None, matchTimeoutMilliseconds: 500)]
    private static partial Regex InlineCode();

    [GeneratedRegex(@"\*\*([^*]+)\*\*|__([^_]+)__", RegexOptions.None, matchTimeoutMilliseconds: 500)]
    private static partial Regex Bold();

    [GeneratedRegex(@"(?<![\w*])\*([^*\s][^*]*)\*(?![\w*])|(?<![\w_])_([^_\s][^_]*)_(?![\w_])", RegexOptions.None, matchTimeoutMilliseconds: 500)]
    private static partial Regex Italic();

    [GeneratedRegex(@"\[([^\]]+)\]\((https?://[^\s)]+)\)", RegexOptions.None, matchTimeoutMilliseconds: 500)]
    private static partial Regex Link();

    public static string ToHtml(string? markdown)
    {
        var lines = (markdown ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var html = new StringBuilder();
        var paragraph = new List<string>();
        string? list = null; // "ol" or "ul"
        var fence = false;
        var code = new StringBuilder();

        void FlushParagraph()
        {
            if (paragraph.Count > 0)
            {
                html.Append("<p>").Append(Inline(string.Join(' ', paragraph))).Append("</p>");
                paragraph.Clear();
            }
        }

        void CloseList()
        {
            if (list is not null)
            {
                html.Append("</").Append(list).Append('>');
                list = null;
            }
        }

        foreach (var raw in lines)
        {
            if (raw.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                if (fence)
                {
                    html.Append("<pre><code>").Append(WebUtility.HtmlEncode(code.ToString().TrimEnd('\n'))).Append("</code></pre>");
                    code.Clear();
                    fence = false;
                }
                else
                {
                    FlushParagraph();
                    CloseList();
                    fence = true;
                }

                continue;
            }

            if (fence)
            {
                code.Append(raw).Append('\n');
                continue;
            }

            var line = raw.TrimEnd();
            if (line.Length == 0)
            {
                FlushParagraph();
                CloseList();
                continue;
            }

            if (Heading().Match(line) is { Success: true } heading)
            {
                FlushParagraph();
                CloseList();
                var level = Math.Min(6, heading.Groups[1].Length);
                html.Append(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"<h{level}>")).Append(Inline(heading.Groups[2].Value))
                    .Append(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"</h{level}>"));
            }
            else if (Ordered().Match(line) is { Success: true } ordered)
            {
                FlushParagraph();
                if (list != "ol")
                {
                    CloseList();
                    html.Append("<ol>");
                    list = "ol";
                }

                html.Append("<li>").Append(Inline(ordered.Groups[1].Value)).Append("</li>");
            }
            else if (Bullet().Match(line) is { Success: true } bullet)
            {
                FlushParagraph();
                if (list != "ul")
                {
                    CloseList();
                    html.Append("<ul>");
                    list = "ul";
                }

                html.Append("<li>").Append(Inline(bullet.Groups[1].Value)).Append("</li>");
            }
            else if (line.StartsWith('>'))
            {
                FlushParagraph();
                CloseList();
                html.Append("<blockquote>").Append(Inline(line.TrimStart('>', ' '))).Append("</blockquote>");
            }
            else if (line is "---" or "***" or "___")
            {
                FlushParagraph();
                CloseList();
                html.Append("<hr/>");
            }
            else
            {
                CloseList();
                paragraph.Add(line.Trim());
            }
        }

        if (fence)
        {
            html.Append("<pre><code>").Append(WebUtility.HtmlEncode(code.ToString().TrimEnd('\n'))).Append("</code></pre>");
        }

        FlushParagraph();
        CloseList();
        return html.ToString();
    }

    /// <summary>Inline formatting on text that is encoded first, so only the tags below can ever appear.</summary>
    private static string Inline(string text)
    {
        var encoded = WebUtility.HtmlEncode(text);
        encoded = InlineCode().Replace(encoded, "<code>$1</code>");
        encoded = Link().Replace(encoded, "<a href=\"$2\" target=\"_blank\" rel=\"noopener noreferrer\">$1</a>");
        encoded = Bold().Replace(encoded, m => $"<strong>{(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)}</strong>");
        return Italic().Replace(encoded, m => $"<em>{(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)}</em>");
    }
}
