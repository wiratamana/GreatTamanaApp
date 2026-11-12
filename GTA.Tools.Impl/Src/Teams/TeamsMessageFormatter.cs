
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace GTA.Tools.Impl.Teams;

/// <summary>How the text of an outgoing message is read.</summary>
public enum TeamsTextFormat
{
    /// <summary>Exactly as written. Nothing is interpreted. Blank lines and indentation are kept.</summary>
    Text,

    /// <summary>Markdown subset (see <see cref="TeamsMessageFormatter"/>), converted to the HTML that Teams shows.</summary>
    Markdown,

    /// <summary>Already HTML. Sent as it is. Teams removes what it does not support.</summary>
    Html,
}

/// <summary>
/// Turns the text of an outgoing message into the HTML body Teams expects.
/// Markdown subset: # headings, **bold**, *italic*, ~~strike~~, `code`, ``` code blocks, - / 1. lists (nested by indent),
/// &gt; quotes, [text](https://url), --- rule, and | tables |. Anything else stays plain text.
/// </summary>
public static class TeamsMessageFormatter
{
    private static readonly Regex ListLine = new(@"^(\s*)([-*+]|\d+[.)])\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex HeadingLine = new(@"^(#{1,6})\s+(.*?)\s*#*\s*$", RegexOptions.Compiled);
    private static readonly Regex RuleLine = new(@"^\s{0,3}([-*_])(\s*\1){2,}\s*$", RegexOptions.Compiled);
    private static readonly Regex TableSeparator = new(@"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$", RegexOptions.Compiled);

    public static string ToHtml(string text, TeamsTextFormat format) => format switch
    {
        TeamsTextFormat.Html => text,
        TeamsTextFormat.Markdown => MarkdownToHtml(text),
        _ => PlainToHtml(text),
    };

    /// <summary>One paragraph per line. A blank line stays a blank line, leading and double spaces stay.</summary>
    private static string PlainToHtml(string text)
    {
        var sb = new StringBuilder();
        foreach (string line in SplitLines(text))
            sb.Append("<p>").Append(line.Length == 0 ? "&nbsp;" : EncodeKeepSpaces(line)).Append("</p>");
        return sb.ToString();
    }

    private static string EncodeKeepSpaces(string line)
    {
        string encoded = WebUtility.HtmlEncode(line.Replace("\t", "    "));
        var sb = new StringBuilder(encoded.Length);
        for (int i = 0; i < encoded.Length; i++)
        {
            bool keep = encoded[i] == ' ' && (i == 0 || encoded[i - 1] == ' ' || i + 1 >= encoded.Length || encoded[i + 1] == ' ');
            sb.Append(keep ? "&nbsp;" : encoded[i].ToString());
        }
        return sb.ToString();
    }

    private static string[] SplitLines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    #region Markdown

    private static string MarkdownToHtml(string markdown)
    {
        string[] lines = SplitLines(markdown);
        var html = new StringBuilder();
        int i = 0;

        while (i < lines.Length)
        {
            string line = lines[i];

            if (string.IsNullOrWhiteSpace(line)) { i++; continue; }

            // Fenced code block
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                var code = new List<string>();
                i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
                    code.Add(lines[i++]);
                if (i < lines.Length) i++; // closing fence
                html.Append("<pre><code>").Append(WebUtility.HtmlEncode(string.Join("\n", code))).Append("</code></pre>");
                continue;
            }

            Match heading = HeadingLine.Match(line);
            if (heading.Success)
            {
                int level = heading.Groups[1].Length;
                html.Append($"<h{level}>").Append(Inline(heading.Groups[2].Value)).Append($"</h{level}>");
                i++;
                continue;
            }

            if (RuleLine.IsMatch(line)) { html.Append("<hr>"); i++; continue; }

            // Table: a header row followed by a |---|---| row
            if (line.Contains('|') && i + 1 < lines.Length && TableSeparator.IsMatch(lines[i + 1]) && lines[i + 1].Contains('-'))
            {
                i = AppendTable(lines, i, html);
                continue;
            }

            if (line.TrimStart().StartsWith('>'))
            {
                var quote = new List<string>();
                while (i < lines.Length && lines[i].TrimStart().StartsWith('>'))
                    quote.Add(lines[i++].TrimStart().TrimStart('>').TrimStart());
                html.Append("<blockquote>").Append(string.Join("<br>", quote.Select(Inline))).Append("</blockquote>");
                continue;
            }

            if (ListLine.IsMatch(line))
            {
                i = AppendList(lines, i, html);
                continue;
            }

            // Paragraph: lines up to the next blank line or block start
            var para = new List<string>();
            while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]) && !IsBlockStart(lines, i))
                para.Add(lines[i++]);
            if (para.Count == 0) para.Add(lines[i++]); // safety: never loop forever
            html.Append("<p>").Append(string.Join("<br>", para.Select(l => Inline(l.Trim())))).Append("</p>");
        }

        return html.ToString();
    }

    private static bool IsBlockStart(string[] lines, int i)
    {
        string line = lines[i];
        return line.TrimStart().StartsWith("```", StringComparison.Ordinal)
               || HeadingLine.IsMatch(line)
               || RuleLine.IsMatch(line)
               || line.TrimStart().StartsWith('>')
               || ListLine.IsMatch(line)
               || (line.Contains('|') && i + 1 < lines.Length && TableSeparator.IsMatch(lines[i + 1]) && lines[i + 1].Contains('-'));
    }

    private static int AppendList(string[] lines, int i, StringBuilder html)
    {
        // Stack of open lists: (indent of its items, closing tag). Deeper indent = nested list.
        var open = new Stack<(int Indent, string Tag)>();

        while (i < lines.Length)
        {
            Match m = ListLine.Match(lines[i]);
            if (!m.Success) break;

            int indent = m.Groups[1].Value.Replace("\t", "    ").Length;
            string tag = char.IsDigit(m.Groups[2].Value[0]) ? "ol" : "ul";

            while (open.Count > 0 && indent < open.Peek().Indent)
            {
                html.Append("</li></").Append(open.Pop().Tag).Append('>');
            }

            if (open.Count == 0 || indent > open.Peek().Indent)
            {
                html.Append('<').Append(tag).Append('>');
                open.Push((indent, tag));
            }
            else
            {
                html.Append("</li>");
            }

            html.Append("<li>").Append(Inline(m.Groups[3].Value));
            i++;
        }

        while (open.Count > 0)
            html.Append("</li></").Append(open.Pop().Tag).Append('>');
        return i;
    }

    private static int AppendTable(string[] lines, int i, StringBuilder html)
    {
        html.Append("<table><thead><tr>");
        foreach (string cell in SplitRow(lines[i]))
            html.Append("<th>").Append(Inline(cell)).Append("</th>");
        html.Append("</tr></thead><tbody>");
        i += 2; // header + separator

        while (i < lines.Length && lines[i].Contains('|') && !string.IsNullOrWhiteSpace(lines[i]))
        {
            html.Append("<tr>");
            foreach (string cell in SplitRow(lines[i]))
                html.Append("<td>").Append(Inline(cell)).Append("</td>");
            html.Append("</tr>");
            i++;
        }
        html.Append("</tbody></table>");
        return i;
    }

    private static IEnumerable<string> SplitRow(string row)
    {
        string t = row.Trim();
        if (t.StartsWith('|')) t = t[1..];
        if (t.EndsWith('|')) t = t[..^1];
        return t.Split('|').Select(c => c.Trim());
    }

    /// <summary>Inline marks. The text is HTML-encoded FIRST, so it can never inject markup.</summary>
    private static string Inline(string text)
    {
        string s = WebUtility.HtmlEncode(text);

        // Protect code spans from the other rules.
        var codes = new List<string>();
        s = Regex.Replace(s, "`([^`]+)`", m =>
        {
            codes.Add("<code>" + m.Groups[1].Value + "</code>");
            return "\u0001" + (codes.Count - 1) + "\u0001";
        });

        s = Regex.Replace(s, @"\[([^\]]+)\]\((https?://[^\s)]+)\)", "<a href=\"$2\">$1</a>");
        s = Regex.Replace(s, @"\*\*(?!\s)(.+?)(?<!\s)\*\*", "<b>$1</b>");
        s = Regex.Replace(s, @"(?<![\w])__(?!\s)(.+?)(?<!\s)__(?![\w])", "<b>$1</b>");
        s = Regex.Replace(s, @"~~(?!\s)(.+?)(?<!\s)~~", "<s>$1</s>");
        s = Regex.Replace(s, @"(?<![\w*])\*(?![\s*])(.+?)(?<![\s*])\*(?![\w*])", "<i>$1</i>");
        s = Regex.Replace(s, @"(?<![\w])_(?![\s_])(.+?)(?<![\s_])_(?![\w])", "<i>$1</i>");

        return Regex.Replace(s, "\u0001(\\d+)\u0001", m => codes[int.Parse(m.Groups[1].Value)]);
    }

    #endregion
}
