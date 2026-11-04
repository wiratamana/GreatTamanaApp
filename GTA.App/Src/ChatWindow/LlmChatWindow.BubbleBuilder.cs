using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using GTA.Core;

namespace GTA.App;

public partial class LlmChatWindow
{
    // ── Message bubble (entry point for session reload) ───────────────────────

    /// <summary>Build a fully-populated chat bubble for an existing ChatMessage</summary>
    private Control BuildMessageBubble(ChatSession session, ChatMessage message)
    {
        bool isUser = message.Role == "user";

        var timestamp = new TextBlock
        {
            Text                = message.Timestamp.ToString("HH:mm"),
            FontSize            = 10,
            Foreground          = ColMuted,
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Margin              = new Thickness(0, 0, 0, 3),
        };

        Control bubbleContent;

        if (isUser)
        {
            if (message.AttachedImages.Count > 0)
            {
                var userPanel = new StackPanel { Spacing = 8 };

                // Memory fix: attached-image Bitmaps are no longer decoded eagerly here
                foreach (var imgPath in message.AttachedImages)
                {
                    if (!File.Exists(imgPath))
                    {
                        // Cheap existence check only
                        userPanel.Children.Add(new TextBlock
                        {
                            Text      = $"[Image: {Path.GetFileName(imgPath)}]",
                            FontSize  = 12,
                            Foreground = Brushes.White,
                            FontStyle = FontStyle.Italic,
                        });
                        continue;
                    }

                    var img = new Image
                    {
                        MaxWidth            = 320,
                        MaxHeight           = 240,
                        Stretch             = Stretch.Uniform,
                        HorizontalAlignment = HorizontalAlignment.Right,
                    };
                    userPanel.Children.Add(img);

                    var host = new LazyAttachedImageHost(img, imgPath);
                    session.Runtime.AttachedImageHosts.Add(host);

                    if (ReferenceEquals(session, _current))
                        host.Load();
                }

                if (!string.IsNullOrEmpty(message.Content))
                {
                    userPanel.Children.Add(new SelectableTextBlock
                    {
                        Text         = message.Content,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground   = Brushes.White,
                        FontSize     = 14,
                        LineHeight   = 22,
                    });
                }

                bubbleContent = userPanel;
            }
            else
            {
                bubbleContent = new SelectableTextBlock
                {
                    Text         = message.Content,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground   = Brushes.White,
                    FontSize     = 14,
                    LineHeight   = 22,
                };
            }
        }
        else
        {
            // ── Timeline fix: reconstruct the full turn widget from saved history ───
            var contentPanel = new StackPanel { Spacing = 6 };

            if (message.TextSegments.Count > 0 || message.Rounds.Count > 0)
            {
                // NEW SESSIONS: full timeline reconstruction via SequenceIndex
                var events = new List<(int seq, string type, object data)>(
                    message.ThinkingBlocks.Count + message.ToolCalls.Count + message.TextSegments.Count
                    + message.PromptInjections.Count + message.SubAgentDispatches.Count
                    + message.Rounds.Count);

                foreach (var tb in message.ThinkingBlocks)
                    events.Add((tb.SequenceIndex, "thinking", tb));
                foreach (var tc in message.ToolCalls)
                    events.Add((tc.SequenceIndex, "toolcall", tc));
                foreach (var seg in message.TextSegments)
                    events.Add((seg.SequenceIndex, "text", seg));
                foreach (var pi in message.PromptInjections)
                    events.Add((pi.SequenceIndex, "injection", pi));
                foreach (var sad in message.SubAgentDispatches)
                    events.Add((sad.SequenceIndex, "subagent", sad));
                foreach (var rd in message.Rounds)
                    events.Add((rd.SequenceIndex, "round", rd));

                events.Sort((a, b) => a.seq.CompareTo(b.seq));

                // Everything after a round marker goes into that round's row (number on the left).
                // Before the first marker (or in sessions saved without rounds) items go straight into contentPanel.
                StackPanel target = contentPanel;

                foreach (var (_, type, data) in events)
                {
                    switch (type)
                    {
                        case "round":
                        {
                            var row = BuildRoundRow((DisplayRound)data);
                            contentPanel.Children.Add(row.Root);
                            target = row.Items;
                            break;
                        }

                        case "thinking":
                        {
                            var tb = (DisplayThinkingBlock)data;
                            var thinkingTb = new SelectableTextBlock
                            {
                                Text         = tb.Text,
                                TextWrapping = TextWrapping.Wrap,
                                Foreground   = ColMuted,
                                FontSize     = 12,
                                FontFamily   = new FontFamily("Consolas, Courier New, monospace"),
                                LineHeight   = 18,
                                Opacity      = 0.9,
                            };
                            target.Children.Add(BuildCollapsibleThinkingSection(thinkingTb));
                            break;
                        }

                        case "toolcall":
                            target.Children.Add(BuildStaticToolCallSection((DisplayToolCall)data));
                            break;

                        case "injection":
                            target.Children.Add(BuildPromptInjectionWidget((DisplayPromptInjection)data));
                            break;

                        case "subagent":
                            target.Children.Add(BuildSubAgentDispatchWidget((DisplaySubAgentDispatch)data));
                            break;

                        case "text":
                        {
                            var seg = (DisplayTextSegment)data;
                            if (!string.IsNullOrEmpty(seg.Text))
                            {
                                if (target.Children.Count > 0)
                                {
                                    target.Children.Add(new Border
                                    {
                                        Height     = 1,
                                        Background = ColDivider,
                                        Opacity    = 0.35,
                                        Margin     = new Thickness(0, 2, 0, 2),
                                    });
                                }
                                var textPanel = new StackPanel { Spacing = 4 };
                                target.Children.Add(textPanel);
                                RebuildMarkdownPanel(textPanel, seg.Text);
                            }
                            break;
                        }
                    }
                }
            }
            else
            {
                // OLD SESSIONS: timestamp-based ordering (backward compatible)
                var timedEvents = new List<(DateTimeOffset ts, string type, object data)>(
                    message.ThinkingBlocks.Count + message.ToolCalls.Count);

                foreach (var tb in message.ThinkingBlocks)
                    timedEvents.Add((tb.Timestamp, "thinking", tb));
                foreach (var tc in message.ToolCalls)
                    timedEvents.Add((tc.StartedAt, "toolcall", tc));

                timedEvents.Sort((a, b) => a.ts.CompareTo(b.ts));

                foreach (var (_, type, data) in timedEvents)
                {
                    switch (type)
                    {
                        case "thinking":
                        {
                            var tb = (DisplayThinkingBlock)data;
                            var thinkingTb = new SelectableTextBlock
                            {
                                Text         = tb.Text,
                                TextWrapping = TextWrapping.Wrap,
                                Foreground   = ColMuted,
                                FontSize     = 12,
                                FontFamily   = new FontFamily("Consolas, Courier New, monospace"),
                                LineHeight   = 18,
                                Opacity      = 0.9,
                            };
                            contentPanel.Children.Add(BuildCollapsibleThinkingSection(thinkingTb));
                            break;
                        }
                        case "toolcall":
                            contentPanel.Children.Add(BuildStaticToolCallSection((DisplayToolCall)data));
                            break;
                    }
                }

                if ((message.ThinkingBlocks.Count > 0 || message.ToolCalls.Count > 0)
                    && !string.IsNullOrEmpty(message.Content))
                {
                    contentPanel.Children.Add(new Border
                    {
                        Height     = 1,
                        Background = ColDivider,
                        Opacity    = 0.35,
                        Margin     = new Thickness(0, 2, 0, 2),
                    });
                }

                if (!string.IsNullOrEmpty(message.Content))
                {
                    var textPanel = new StackPanel { Spacing = 4 };
                    contentPanel.Children.Add(textPanel);
                    RebuildMarkdownPanel(textPanel, message.Content);
                }
            }

            if (message.Usage.HasValue)
            {
                contentPanel.Children.Add(BuildTokenUsageRow(
                    message.Usage.Value, isFinal: true, message.CostUsd));
            }

            bubbleContent = contentPanel;
        }

        var bubble = new Border
        {
            CornerRadius        = isUser
                                  ? new CornerRadius(18, 18, 4, 18)
                                  : new CornerRadius(18, 18, 18, 4),
            Padding             = new Thickness(14, 10, 14, 10),
            Background          = isUser ? BgUserBubble : BgAiBubble,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child               = bubbleContent,
        };

        Control? roleLabel = null;
        if (!isUser)
        {
            roleLabel = BuildAssistantHeader(() =>
            {
                if (!string.IsNullOrEmpty(message.Content)) return message.Content;
                var sb = new System.Text.StringBuilder();
                foreach (var seg in message.TextSegments)
                {
                    if (sb.Length > 0) sb.Append("\n\n");
                    sb.Append(seg.Text);
                }
                return sb.ToString();
            });
        }

        var wrapper = new StackPanel
        {
            // Full width for BOTH user and AI bubbles — follows the window size.
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        if (roleLabel != null) wrapper.Children.Add(roleLabel);
        wrapper.Children.Add(bubble);

        // JIRA auto-injection widgets
        if (isUser)
        {
            foreach (var ji in message.JiraInjections)
            {
                var (jiraWrapper, _) = BuildJiraInjectionWidget(ji.TicketKey, ji.Content);
                wrapper.Children.Add(jiraWrapper);
            }
        }

        wrapper.Children.Add(timestamp);

        return wrapper;
    }

    // ── Assistant header: "✦ Assistant" + "⧉ Copy" button ─────────────────────

    /// <summary>The small header line above an assistant bubble</summary>
    internal static Control BuildAssistantHeader(Func<string> getText)
    {
        var label = new TextBlock
        {
            Text              = "✦ Assistant",
            FontSize          = 11,
            FontWeight        = FontWeight.SemiBold,
            Foreground        = ColMuted,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(4, 0, 0, 0),
        };

        var copyText = new TextBlock { Text = "⧉ Copy" };
        var copyBtn = new Button
        {
            Content           = copyText,
            FontSize          = 10,
            BorderThickness   = new Thickness(0),
            Padding           = new Thickness(6, 0, 6, 0),
            MinHeight         = 0,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor            = new Cursor(StandardCursorType.Hand),
            Opacity           = 0.45,
        };
        UiTheme.StyleLinkButton(copyBtn, ColMuted);
        ToolTip.SetTip(copyBtn, "Copy this whole reply (text only) to the clipboard");

        bool busy = false;
        copyBtn.Click += async (_, _) =>
        {
            if (busy) return;
            try
            {
                string text = getText();
                if (string.IsNullOrEmpty(text)) return;

                var clipboard = TopLevel.GetTopLevel(copyBtn)?.Clipboard;
                if (clipboard == null) return;

                busy = true;
                await clipboard.SetTextAsync(text);
                copyText.Text = "✓ Copied";
                await System.Threading.Tasks.Task.Delay(1400);
                copyText.Text = "⧉ Copy";
            }
            catch { /* ignore clipboard errors */ }
            finally { busy = false; }
        };

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin            = new Thickness(0, 0, 0, 2),
            Background        = Brushes.Transparent,   // so hover works on the whole line
        };
        Grid.SetColumn(label,   0);
        Grid.SetColumn(copyBtn, 1);
        grid.Children.Add(label);
        grid.Children.Add(copyBtn);

        grid.PointerEntered += (_, _) => copyBtn.Opacity = 1.0;
        grid.PointerExited  += (_, _) => copyBtn.Opacity = 0.45;

        return grid;
    }

    // Collapsible thinking section

    private Control BuildCollapsibleThinkingSection(TextBlock thinkingTb)
        => new ThinkingSection(thinkingTb, live: false).Root;

    // ── Round row: round number as a small pill in the left gutter, the round's items on the right ──────

    /// <summary>Width of the left gutter that holds the round-number pill (and the thin timeline line under it).</summary>
    private const double RoundGutterWidth = 30;

    // Round pill colours (accent-tinted, so it looks like part of the app theme)
    private static readonly SolidColorBrush RoundPillBg        = new(Color.FromArgb(40,  88, 101, 242));
    private static readonly SolidColorBrush RoundPillBgHot     = new(Color.FromArgb(105, 88, 101, 242));
    private static readonly SolidColorBrush RoundPillBorder    = new(Color.FromArgb(110, 130, 145, 255));
    private static readonly SolidColorBrush RoundPillBorderHot = new(Color.FromRgb(150, 162, 255));
    private static readonly SolidColorBrush RoundPillFg        = new(Color.FromRgb(170, 180, 255));
    private static readonly SolidColorBrush RoundLineBrush     = new(Color.FromArgb(55,  130, 145, 255));

    private sealed class RoundRow
    {
        public Grid       Root  { get; init; } = null!;
        /// <summary>Thinking / tool calls / text of this round go here.</summary>
        public StackPanel Items { get; init; } = null!;
        /// <summary>Raw info-log lines of this round (hidden until the number is clicked).</summary>
        public StackPanel Log   { get; init; } = null!;
        /// <summary>The round-number pill. Carries the hover popup (In / Out / Read / Write / $).</summary>
        public Control    Badge { get; init; } = null!;
    }

    /// <summary>
    /// Build one round row: the round number sits in a small pill left of the round's first item, with a thin
    /// timeline line running down under it. Hover the pill for In / Out / Read / Write / $ of that round.
    /// Click it to show/hide the round's raw info-log lines (the pill stays lit while they are shown).
    /// Used by BOTH the live stream and the session reload, so they look the same.
    /// </summary>
    private static RoundRow BuildRoundRow(DisplayRound round)
    {
        var log = new StackPanel { Spacing = 2 };
        var logBorder = new Border
        {
            IsVisible       = false,
            Background      = BgCodeBlock,
            BorderBrush     = ColDivider,
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(6),
            Padding         = new Thickness(12, 8, 12, 8),
            Margin          = new Thickness(0, 0, 0, 2),
            Child           = log,
        };

        var items = new StackPanel { Spacing = 6 };

        // ── the pill ──
        var numText = new TextBlock
        {
            Text                = round.Round.ToString(),
            FontSize            = 10,
            FontWeight          = FontWeight.SemiBold,
            FontFamily          = new FontFamily("Consolas, Courier New, monospace"),
            Foreground          = RoundPillFg,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
        };
        var pill = new Border
        {
            MinWidth            = 20,
            Height              = 18,
            CornerRadius        = new CornerRadius(9),
            Padding             = new Thickness(5, 0, 5, 0),
            Background          = RoundPillBg,
            BorderBrush         = RoundPillBorder,
            BorderThickness     = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Top,
            Margin              = new Thickness(0, 2, 0, 0),
            Cursor              = new Cursor(StandardCursorType.Hand),
            Child               = numText,
        };
        ToolTip.SetTip(pill, BuildRoundTooltip(round));

        bool hovered = false;
        void Refresh()
        {
            bool hot = hovered || logBorder.IsVisible;
            pill.Background      = hot ? RoundPillBgHot : RoundPillBg;
            pill.BorderBrush     = hot ? RoundPillBorderHot : RoundPillBorder;
            numText.Foreground   = hot ? Brushes.White : RoundPillFg;
        }
        pill.PointerEntered += (_, _) => { hovered = true;  Refresh(); };
        pill.PointerExited  += (_, _) => { hovered = false; Refresh(); };
        pill.PointerPressed += (_, _) =>
        {
            if (log.Children.Count > 0)
            {
                logBorder.IsVisible = !logBorder.IsVisible;
                Refresh();
            }
        };

        // ── thin timeline line under the pill (ties the pill to the round's items) ──
        var line = new Border
        {
            Width               = 1,
            Background          = RoundLineBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Stretch,
            Margin              = new Thickness(0, 3, 0, 2),
        };

        var gutter = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        Grid.SetRow(pill, 0);
        Grid.SetRow(line, 1);
        gutter.Children.Add(pill);
        gutter.Children.Add(line);

        var column = new StackPanel { Spacing = 2 };
        column.Children.Add(logBorder);
        column.Children.Add(items);

        var root = new Grid { ColumnDefinitions = new ColumnDefinitions($"{RoundGutterWidth},*") };
        Grid.SetColumn(gutter, 0);
        Grid.SetColumn(column, 1);
        root.Children.Add(gutter);
        root.Children.Add(column);

        return new RoundRow { Root = root, Items = items, Log = log, Badge = pill };
    }

    /// <summary>
    /// Hover popup of a round number: elapsed time + In / Out / Read / Write / $ chips.
    /// The default Fluent ToolTip has a MaxWidth (~320 px) and CLIPS anything wider. A horizontal StackPanel of chips
    /// does not wrap, so the last chip ("$ ...") was cut off. A WrapPanel with its own MaxWidth under that limit
    /// puts the chips on a second line instead, so nothing is ever cut.
    /// </summary>
    internal static Control BuildRoundTooltip(DisplayRound round)
    {
        // Must stay below the ToolTip's own MaxWidth minus its padding + our padding.
        const double ContentMaxWidth = 270;

        var panel = new StackPanel { Spacing = 5, MaxWidth = ContentMaxWidth };

        string head = $"Round {round.Round}";
        if (round.ElapsedSeconds.HasValue)
            head += $"  ·  {round.ElapsedSeconds.Value:F1}s";
        panel.Children.Add(new TextBlock
        {
            Text       = head,
            FontSize   = 11,
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            Foreground = ColText,
            FontWeight = FontWeight.SemiBold,
        });

        if (round.Usage.HasValue)
        {
            var chips = new WrapPanel { Orientation = Orientation.Horizontal, MaxWidth = ContentMaxWidth };
            FillTokenChips(chips, round.Usage.Value, round.CostUsd);
            foreach (var chip in chips.Children)
                if (chip is Control c) c.Margin = new Thickness(0, 0, 4, 4);   // spacing (works on every Avalonia version)
            panel.Children.Add(chips);
        }
        else
        {
            panel.Children.Add(new TextBlock
            {
                Text         = "No token usage recorded for this round",
                FontSize     = 11,
                Foreground   = ColMuted,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        return new Border
        {
            Background      = BgCodeBlock,
            BorderBrush     = ColDivider,
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(6),
            Padding         = new Thickness(8, 6, 8, 2),
            Child           = panel,
        };
    }

    /// <summary>
    /// Where a new thinking block / tool call / text goes: the CURRENT round's items panel while a round is
    /// open (so it lines up with the round number), otherwise the turn panel itself.
    /// </summary>
    private static StackPanel RoundTarget(ChatSessionRuntime runtime, StackPanel turnPanel)
        => ReferenceEquals(runtime.CurrentTurnContentPanel, turnPanel) && runtime.CurrentRoundItemsPanel != null
            ? runtime.CurrentRoundItemsPanel
            : turnPanel;

    // ── Static tool-call section (reloaded sessions) ──────────────────────────

    /// <summary>Build a collapsible tool-call widget pre-populated with saved parameter and result data.</summary>
    private static Control BuildStaticToolCallSection(DisplayToolCall tc)
    {
        // File-writing IO tools (write_file, edit_line, …) replay as the same
        // fancy Write/Edit card used while streaming — see FileToolCard.cs.
        if (FileToolCard.TryGetKind(tc.ToolName, out var fileCardKind))
        {
            var card = new FileToolCard(fileCardKind, live: false);
            card.OnInputComplete(tc.InputJson ?? "{}");
            if (string.IsNullOrEmpty(tc.Result)) card.MarkNoResult();
            else                                 card.SetResult(tc.Result);
            return card.Root;
        }

        // Parameters as clean "name  value" rows (display only — the saved JSON is untouched).
        var paramsHolder = new StackPanel { Spacing = 2 };
        paramsHolder.Children.Add(ToolCallSection.BuildReadableParams(tc.InputJson, tc.ToolName));

        var resultPanel = new StackPanel { Spacing = 2 };
        var lazyResult  = new LazyToolResultHost(resultPanel);
        lazyResult.SetResult(tc.Result, isExpanded: false);

        var contentPanel = ToolCallSection.BuildBody(
            paramsHolder, resultPanel, isDispatch: tc.ToolName == "dispatch_sub_agent");

        var section = new ToolCallSection(tc.ToolName, contentPanel,
            expanded =>
            {
                if (expanded) lazyResult.OnExpand();
                else          lazyResult.OnCollapse();
            },
            live: false);

        section.SetParams(tc.InputJson);   // one-line summary in the header
        TimeSpan? took = tc.CompletedAt > tc.StartedAt ? tc.CompletedAt - tc.StartedAt : null;
        section.MarkStatic(tc.Result, took);   // ✓ / ✗ + saved duration

        return section.Root;
    }
}
