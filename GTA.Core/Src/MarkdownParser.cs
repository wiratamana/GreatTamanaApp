namespace GTA.Core;

public enum MdBlockType { Paragraph, Heading, HRule, BulletItem, CodeFence, Blockquote, Table }

public sealed class MdBlock
{
    public MdBlockType Type     { get; init; }
    public int         Level    { get; init; }          // 1-4 for Heading; 0 otherwise
    public string[]    Lines    { get; init; } = [];    // text lines for this block
    public string      Language { get; init; } = "";    // language hint for CodeFence blocks
}

public static class MarkdownParser
{
    /// <summary>Parse <paramref name="text"/> into a flat list of typed <see cref="MdBlock"/> objects.</summary>
    public static List<MdBlock> ParseMdBlocks(string text)
    {
        // Normalise line endings and collapse runs of 3+ blank lines → 2
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        while (text.Contains("\n\n\n"))
            text = text.Replace("\n\n\n", "\n\n");

        var blocks     = new List<MdBlock>();
        var paraLines  = new List<string>();   // accumulates paragraph lines
        var quoteLines = new List<string>();   // accumulates blockquote lines ("> …")
        var tableLines = new List<string>();   // accumulates table rows ("|…|")

        // ── Code-fence state ─────────────────────────────────────────────────
        bool         inCodeFence   = false;
        string       fenceMarker   = "";       // "```" or "~~~"
        string       codeFenceLang = "";       // language tag after opening fence
        List<string> codeLines     = new();    // raw lines inside the fence

        void FlushParagraph()
        {
            if (paraLines.Count == 0) return;
            blocks.Add(new MdBlock { Type = MdBlockType.Paragraph, Lines = paraLines.ToArray() });
            paraLines.Clear();
        }

        void FlushCodeFence()
        {
            if (!inCodeFence) return;
            blocks.Add(new MdBlock
            {
                Type     = MdBlockType.CodeFence,
                Language = codeFenceLang,
                Lines    = codeLines.ToArray(),
            });
            inCodeFence   = false;
            fenceMarker   = "";
            codeFenceLang = "";
            codeLines.Clear();
        }

        void FlushQuote()
        {
            if (quoteLines.Count == 0) return;
            blocks.Add(new MdBlock { Type = MdBlockType.Blockquote, Lines = quoteLines.ToArray() });
            quoteLines.Clear();
        }

        void FlushTable()
        {
            if (tableLines.Count == 0) return;
            blocks.Add(new MdBlock { Type = MdBlockType.Table, Lines = tableLines.ToArray() });
            tableLines.Clear();
        }

        foreach (var rawLine in text.Split('\n'))
        {
            var line    = rawLine.TrimEnd();
            var trimmed = line.TrimStart();

            // ── Inside a code fence ──────────────────────────────────────────
            if (inCodeFence)
            {
                bool isClosingFence = fenceMarker == "```"
                    ? (trimmed == "```" || (trimmed.Length >= 3 && trimmed.Replace("`", "").Length == 0))
                    : (trimmed == "~~~" || (trimmed.Length >= 3 && trimmed.Replace("~", "").Length == 0));

                if (isClosingFence)
                {
                    FlushCodeFence();
                    continue;
                }

                codeLines.Add(rawLine.TrimEnd());
                continue;
            }

            // ── Code-fence start: lines beginning with ``` or ~~~ ───────────
            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                FlushParagraph();
                FlushQuote();
                FlushTable();
                inCodeFence   = true;
                fenceMarker   = trimmed.StartsWith("```") ? "```" : "~~~";
                codeFenceLang = trimmed[fenceMarker.Length..].Trim();
                codeLines.Clear();
                continue;
            }

            // ── Blank line → all block boundaries ───────────────────────────
            if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph();
                FlushQuote();
                FlushTable();
                continue;
            }

            // ── Horizontal rule: 3+ identical chars from { - * _ } ──────────
            // Note: must NOT contain | so it is not confused with a table separator.
            if (trimmed.Length >= 3 && !trimmed.Contains('|') &&
                (trimmed.Replace("-", "").Length == 0 ||
                 trimmed.Replace("*", "").Length == 0 ||
                 trimmed.Replace("_", "").Length == 0))
            {
                FlushParagraph();
                FlushQuote();
                FlushTable();
                blocks.Add(new MdBlock { Type = MdBlockType.HRule });
                continue;
            }

            // ── Blockquote: lines starting with "> " or bare ">" ────────────
            if (trimmed.StartsWith("> ") || trimmed == ">")
            {
                FlushParagraph();
                FlushTable();
                string quoteText = trimmed.Length > 2 ? trimmed[2..] : "";
                quoteLines.Add(quoteText);
                continue;
            }
            else
            {
                // Any non-blockquote line ends the current blockquote block.
                FlushQuote();
            }

            // ── Heading: # / ## / ### / #### ────────────────────────────────
            int    hLevel = 0;
            string hText  = line;

            if      (line.StartsWith("#### ")) { hLevel = 4; hText = line[5..]; }
            else if (line.StartsWith("### " )) { hLevel = 3; hText = line[4..]; }
            else if (line.StartsWith("## "  )) { hLevel = 2; hText = line[3..]; }
            else if (line.StartsWith("# "   )) { hLevel = 1; hText = line[2..]; }

            if (hLevel > 0)
            {
                FlushParagraph();
                FlushTable();
                blocks.Add(new MdBlock
                {
                    Type  = MdBlockType.Heading,
                    Level = hLevel,
                    Lines = [hText.Replace("**", "").Trim()],
                });
                continue;
            }

            // ── Table row: line starting with | ──────────────────────────────
            if (trimmed.StartsWith("|") && trimmed.Length > 1)
            {
                FlushParagraph();
                tableLines.Add(trimmed);
                continue;
            }
            else
            {
                // Any non-table line ends the current table block.
                FlushTable();
            }

            // ── Lone bullet / asterisk with no content → skip ────────────────
            if (trimmed == "-" || trimmed == "*")
                continue;

            // ── Bullet item: "- text" or "* text" ───────────────────────────
            if ((trimmed.StartsWith("- ") || trimmed.StartsWith("* ")) &&
                trimmed.Length > 2)
            {
                FlushParagraph();
                int    indent     = line.Length - trimmed.Length;
                string bulletText = new string(' ', indent) + "• " + trimmed[2..];
                blocks.Add(new MdBlock
                {
                    Type  = MdBlockType.BulletItem,
                    Lines = [bulletText],
                });
                continue;
            }

            // ── Regular paragraph line ───────────────────────────────────────
            paraLines.Add(line);
        }

        // Flush anything that was still open when input ended
        if (inCodeFence)
            FlushCodeFence();   // unclosed fence (streaming mid-block) — show what we have
        else
            FlushParagraph();

        FlushQuote();
        FlushTable();

        return blocks;
    }
}
