using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using GTA.Core;

namespace GTA.App;

public partial class LlmChatWindow
{
    // ── Entry point ───────────────────────────────────────────────────────────

    /// <summary>Clear panel and repopulate it with one styled SelectableTextBlock per parsed markdown block.</summary>
    public static void RebuildMarkdownPanel(StackPanel panel, string text, bool streaming = false)
    {
        panel.Children.Clear();

        if (string.IsNullOrEmpty(text))
        {
            if (streaming)
                panel.Children.Add(MakeBodyTextBlock("…"));
            return;
        }

        var blocks = MarkdownParser.ParseMdBlocks(text);

        if (blocks.Count == 0)
        {
            if (streaming)
                panel.Children.Add(MakeBodyTextBlock("…"));
            return;
        }

        var run = new List<MdBlock>();

        void FlushRun()
        {
            if (run.Count == 0) return;
            panel.Children.Add(CreateMergedTextBlock(run));
            run.Clear();
        }

        foreach (var block in blocks)
        {
            if (block.Type is MdBlockType.Heading or MdBlockType.Paragraph or MdBlockType.BulletItem)
            {
                run.Add(block);
            }
            else
            {
                FlushRun();
                panel.Children.Add(CreateBlockControl(block));
            }
        }
        FlushRun();
    }

    /// <summary>One SelectableTextBlock for a run of consecutive heading / paragraph / bullet blocks.</summary>
    private static SelectableTextBlock CreateMergedTextBlock(List<MdBlock> run)
    {
        var tb = new SelectableTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground   = Brushes.White,
            FontSize     = 14,
            LineHeight   = 22,
        };

        var inlines = tb.Inlines;
        if (inlines == null)
        {
            var all = new List<string>();
            foreach (var b in run) all.AddRange(b.Lines);
            tb.Text = string.Join("\n", all);
            return tb;
        }

        for (int bi = 0; bi < run.Count; bi++)
        {
            var block = run[bi];

            if (bi > 0)
            {
                var prev = run[bi - 1];
                inlines.Add(new LineBreak());
                bool gap = block.Type == MdBlockType.Heading
                        || (block.Type == MdBlockType.Paragraph && prev.Type != MdBlockType.Heading);
                if (gap) inlines.Add(new LineBreak());
            }

            if (block.Type == MdBlockType.Heading)
            {
                double fs = block.Level switch
                {
                    1 => 22.0,
                    2 => 19.0,
                    3 => 16.0,
                    _ => 14.0,
                };
                inlines.Add(new Run(block.Lines.Length > 0 ? block.Lines[0] : "")
                {
                    FontSize   = fs,
                    FontWeight = FontWeight.Bold,
                    Foreground = Brushes.White,
                });
            }
            else
            {
                for (int i = 0; i < block.Lines.Length; i++)
                {
                    if (i > 0) inlines.Add(new LineBreak());
                    AddInlineMarkdown(inlines, block.Lines[i]);
                }
            }
        }

        return tb;
    }

    // ── Block → Control factory ───────────────────────────────────────────────

    private static Control CreateBlockControl(MdBlock block)
    {
        switch (block.Type)
        {
            case MdBlockType.CodeFence:
                return CreateCodeBlockControl(block);

            case MdBlockType.HRule:
                return new TextBlock
                {
                    Text       = "  ─────────────────────────  ",
                    Foreground = ColHRule,
                    FontSize   = 10,
                    Margin     = new Thickness(0, 2, 0, 2),
                };

            case MdBlockType.Heading:
            {
                double fs = block.Level switch
                {
                    1 => 22.0,
                    2 => 19.0,
                    3 => 16.0,
                    _ => 14.0,
                };
                return new SelectableTextBlock
                {
                    Text         = block.Lines.Length > 0 ? block.Lines[0] : "",
                    FontSize     = fs,
                    FontWeight   = FontWeight.Bold,
                    Foreground   = Brushes.White,
                    TextWrapping = TextWrapping.Wrap,
                    Margin       = new Thickness(0, 6, 0, 2),
                };
            }

            case MdBlockType.BulletItem:
            case MdBlockType.Paragraph:
                return CreateInlineTextBlock(block.Lines);

            case MdBlockType.Blockquote:
                return CreateBlockquoteControl(block);

            case MdBlockType.Table:
                return CreateTableControl(block);

            default:
                return MakeBodyTextBlock(string.Join("\n", block.Lines));
        }
    }

    // ── Code block ────────────────────────────────────────────────────────────

    private static Control CreateCodeBlockControl(MdBlock block)
    {
        string lang = string.IsNullOrWhiteSpace(block.Language) ? "code" : block.Language;
        string code = string.Join("\n", block.Lines);

        var langLabel = new TextBlock
        {
            Text              = lang,
            FontSize          = 11,
            FontFamily        = new FontFamily("Consolas, Courier New, monospace"),
            Foreground        = ColMuted,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(12, 0, 0, 0),
        };

        var copyBtn = new Button
        {
            Content           = "⧉  Copy",
            FontSize          = 11,
            Padding           = new Thickness(8, 4, 8, 4),
            BorderThickness   = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(0, 0, 6, 0),
            Cursor            = new Cursor(StandardCursorType.Hand),
        };
        UiTheme.StyleLinkButton(copyBtn, ColMuted);
        ToolTip.SetTip(copyBtn, "Copy code to clipboard");

        string capturedCode = code;
        copyBtn.Click += async (_, _) =>
        {
            try
            {
                var clipboard = TopLevel.GetTopLevel(copyBtn)?.Clipboard;
                if (clipboard != null)
                {
                    await clipboard.SetTextAsync(capturedCode);
                    copyBtn.Content = "✓  Copied!";
                    UiTheme.StyleLinkButton(copyBtn, new SolidColorBrush(Color.FromRgb(100, 220, 130)));
                    await Task.Delay(1500);
                    copyBtn.Content = "⧉  Copy";
                    UiTheme.StyleLinkButton(copyBtn, ColMuted);
                }
            }
            catch { /* ignore clipboard errors */ }
        };

        var headerGrid = new Grid { Height = 34 };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        Grid.SetColumn(langLabel, 0);
        Grid.SetColumn(copyBtn,   1);
        headerGrid.Children.Add(langLabel);
        headerGrid.Children.Add(copyBtn);

        var headerBorder = new Border
        {
            Background      = BgCodeHeader,
            BorderBrush     = ColDivider,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child           = headerGrid,
        };

        var codeText = new SelectableTextBlock
        {
            Text         = code,
            TextWrapping = TextWrapping.Wrap,
            FontFamily   = new FontFamily("Consolas, Courier New, monospace"),
            FontSize     = 13,
            Foreground   = ColCodeText,
            LineHeight   = 20,
            Margin       = new Thickness(14, 10, 14, 10),
        };

        var codeBorder = new Border
        {
            Background = BgCodeBlock,
            Child      = codeText,
        };

        var outerStack = new StackPanel();
        outerStack.Children.Add(headerBorder);
        outerStack.Children.Add(codeBorder);

        return new Border
        {
            Background      = BgCodeBlock,
            BorderBrush     = ColDivider,
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(8),
            Margin          = new Thickness(0, 6, 0, 6),
            ClipToBounds    = true,
            Child           = outerStack,
        };
    }

    // ── Blockquote ────────────────────────────────────────────────────────────

    private static Control CreateBlockquoteControl(MdBlock block)
    {
        var textPanel = new StackPanel
        {
            Spacing = 4,
            Margin  = new Thickness(12, 0, 0, 0),
        };

        foreach (var quoteLine in block.Lines)
        {
            if (string.IsNullOrWhiteSpace(quoteLine)) continue;

            var tb = new SelectableTextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Foreground   = new SolidColorBrush(Color.FromRgb(200, 190, 230)),
                FontSize     = 14,
                LineHeight   = 22,
                FontStyle    = FontStyle.Italic,
            };

            if (tb.Inlines != null)
                AddInlineMarkdown(tb.Inlines, quoteLine);
            else
                tb.Text = quoteLine;

            textPanel.Children.Add(tb);
        }

        return new Border
        {
            BorderBrush     = ColAccent,
            BorderThickness = new Thickness(3, 0, 0, 0),
            Background      = BgThinking,
            CornerRadius    = new CornerRadius(0, 6, 6, 0),
            Margin          = new Thickness(0, 4, 0, 4),
            Padding         = new Thickness(0, 8, 12, 8),
            Child           = textPanel,
        };
    }

    // ── Table ─────────────────────────────────────────────────────────────────

    private static Control CreateTableControl(MdBlock block)
    {
        var parsedRows = new List<string[]>();
        int headerRowIdx = 0;

        foreach (var rawRow in block.Lines)
        {
            var t = rawRow.Trim();
            if (IsMdTableSeparatorRow(t))
            {
                headerRowIdx = Math.Max(0, parsedRows.Count - 1);
                continue;
            }
            parsedRows.Add(SplitMdTableCells(t));
        }

        if (parsedRows.Count == 0)
            return MakeBodyTextBlock(string.Join("\n", block.Lines));

        int colCount = 0;
        foreach (var row in parsedRows)
            if (row.Length > colCount) colCount = row.Length;

        if (colCount == 0)
            return MakeBodyTextBlock(string.Join("\n", block.Lines));

        var grid = new Grid();

        for (int c = 0; c < colCount; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));

        for (int r = 0; r < parsedRows.Count; r++)
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        for (int r = 0; r < parsedRows.Count; r++)
        {
            bool    isHeader  = r == headerRowIdx;
            string[] rowCells = parsedRows[r];

            for (int c = 0; c < colCount; c++)
            {
                string cellText = c < rowCells.Length ? rowCells[c] : "";

                var cellTb = new SelectableTextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    FontSize     = 13,
                    FontWeight   = isHeader ? FontWeight.Bold : FontWeight.Normal,
                    Foreground   = isHeader ? Brushes.White : ColText,
                    LineHeight   = 20,
                };

                if (cellTb.Inlines != null)
                    AddInlineMarkdown(cellTb.Inlines, cellText);
                else
                    cellTb.Text = cellText;

                bool isLastCol = c == colCount - 1;
                bool isLastRow = r == parsedRows.Count - 1;

                SolidColorBrush cellBg;
                if (isHeader)
                    cellBg = BgCodeHeader;
                else if (r % 2 != 0)
                    cellBg = new SolidColorBrush(Color.FromRgb(38, 38, 52));
                else
                    cellBg = BgAiBubble;

                var cellBorder = new Border
                {
                    BorderBrush     = ColDivider,
                    BorderThickness = new Thickness(0, 0, isLastCol ? 0 : 1, isLastRow ? 0 : 1),
                    Background      = cellBg,
                    Padding         = new Thickness(10, 6, 10, 6),
                    Child           = cellTb,
                };

                Grid.SetRow(cellBorder,    r);
                Grid.SetColumn(cellBorder, c);
                grid.Children.Add(cellBorder);
            }
        }

        return new Border
        {
            BorderBrush     = ColDivider,
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(6),
            Margin          = new Thickness(0, 6, 0, 6),
            ClipToBounds    = true,
            Child           = grid,
        };
    }

    private static bool IsMdTableSeparatorRow(string trimmed)
    {
        if (!trimmed.StartsWith("|")) return false;
        var inner = trimmed.Trim('|');
        if (string.IsNullOrWhiteSpace(inner)) return false;

        foreach (var cell in inner.Split('|'))
        {
            var c = cell.Trim();
            if (c.Length == 0) continue;
            foreach (char ch in c)
                if (ch != '-' && ch != ':' && ch != ' ')
                    return false;
        }
        return true;
    }

    private static string[] SplitMdTableCells(string row)
    {
        var t = row.Trim();
        if (t.StartsWith("|")) t = t[1..];
        if (t.EndsWith("|"))   t = t[..^1];
        var parts = t.Split('|');
        var result = new string[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            result[i] = parts[i].Trim();
        return result;
    }

    // ── Inline text builder ───────────────────────────────────────────────────

    private static SelectableTextBlock CreateInlineTextBlock(string[] lines)
    {
        var tb = new SelectableTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground   = Brushes.White,
            FontSize     = 14,
            LineHeight   = 22,
        };

        var inlines = tb.Inlines;
        if (inlines == null || lines.Length == 0)
        {
            tb.Text = string.Join("\n", lines);
            return tb;
        }

        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0) inlines.Add(new LineBreak());
            AddInlineMarkdown(inlines, lines[i]);
        }

        return tb;
    }

    private static SelectableTextBlock MakeBodyTextBlock(string text) => new SelectableTextBlock
    {
        Text         = text,
        TextWrapping = TextWrapping.Wrap,
        Foreground   = Brushes.White,
        FontSize     = 14,
        LineHeight   = 22,
    };

    // ── Inline markdown (**bold**, `code`) ────────────────────────────────────

    private static void AddInlineMarkdown(InlineCollection inlines, string text)
    {
        int i = 0;
        while (i < text.Length)
        {
            int boldStart = text.IndexOf("**", i, StringComparison.Ordinal);
            int codeStart = text.IndexOf('`', i);

            bool hasBold = boldStart >= 0;
            bool hasCode = codeStart >= 0;

            int  nextIdx;
            bool isBold;

            if (hasBold && (!hasCode || boldStart <= codeStart))
            {
                nextIdx = boldStart; isBold = true;
            }
            else if (hasCode)
            {
                nextIdx = codeStart; isBold = false;
            }
            else
            {
                inlines.Add(new Run(text[i..]));
                break;
            }

            if (nextIdx > i)
                inlines.Add(new Run(text[i..nextIdx]));

            if (isBold)
            {
                int bEnd = text.IndexOf("**", boldStart + 2, StringComparison.Ordinal);
                if (bEnd < 0)
                {
                    inlines.Add(new Run(text[boldStart..]));
                    break;
                }

                string boldText = text[(boldStart + 2)..bEnd];
                if (!string.IsNullOrEmpty(boldText))
                    inlines.Add(new Run(boldText) { FontWeight = FontWeight.Bold });

                i = bEnd + 2;
            }
            else
            {
                int cEnd = text.IndexOf('`', codeStart + 1);
                if (cEnd < 0)
                {
                    inlines.Add(new Run(text[codeStart..]));
                    break;
                }

                string codeContent = text[(codeStart + 1)..cEnd];
                if (!string.IsNullOrEmpty(codeContent))
                    inlines.Add(new Run(codeContent)
                    {
                        FontFamily = new FontFamily("Consolas, Courier New, monospace"),
                        Foreground = ColCodeText,
                    });

                i = cEnd + 1;
            }
        }
    }
}
