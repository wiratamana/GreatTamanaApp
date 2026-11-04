using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GTA.Core;

namespace GTA.App;

public partial class LlmChatWindow
{

    public StackPanel BeginTurnBubble(ChatSession session)
    {
        var runtime = session.Runtime;

        if (runtime.CurrentTurnContentPanel != null)
            return runtime.CurrentTurnContentPanel;

        runtime.ResetTurnScratchState();

        var msg = new ChatMessage { Role = "assistant", Content = "", ModelName = runtime.CurrentTurnModelName };
        session.Messages.Add(msg);
        runtime.CurrentTurnMessage = msg;

        var contentPanel = new StackPanel { Spacing = 6 };
        runtime.CurrentTurnContentPanel = contentPanel;

        runtime.MessagesPanel?.Children.Add(
            BuildBigAssistantBubble(contentPanel, msg.Timestamp, () => msg.Content ?? ""));

        ScrollToBottom(session);

        return contentPanel;
    }

    /// <summary>
    /// Opens a new round row: the number "(N)" on the left, the round's thinking / tool calls / text on the right.
    /// The round is also recorded in the message (DisplayRound) so it is saved and shows again after a restart.
    /// </summary>
    public void BeginRoundSection(ChatSession session, int round)
    {
        var runtime = session.Runtime;
        StackPanel turnPanel = runtime.CurrentTurnContentPanel ?? BeginTurnBubble(session);

        var record = new DisplayRound
        {
            Round         = round,
            SequenceIndex = runtime.CurrentTurnSequence++,
            Timestamp     = DateTimeOffset.Now,
        };
        runtime.CurrentTurnMessage?.Rounds.Add(record);

        var row = BuildRoundRow(record);
        turnPanel.Children.Add(row.Root);

        runtime.CurrentRoundContentPanel = row.Log;
        runtime.CurrentRoundItemsPanel   = row.Items;
        runtime.CurrentRoundBadge        = row.Badge;
        runtime.CurrentRoundRecord       = record;
        runtime.CurrentRoundStopwatch    = new System.Diagnostics.Stopwatch();

        ScrollToBottom(session);
    }

    public TextBlock AddThinkingSectionToBubble(ChatSession session, StackPanel turnPanel)
    {
        var thinkingTb = new SelectableTextBlock
        {
            Text         = "…",
            TextWrapping = TextWrapping.Wrap,
            Foreground   = ColMuted,
            FontSize     = 12,
            FontFamily   = new FontFamily("Consolas, Courier New, monospace"),
            LineHeight   = 18,
            Opacity      = 0.9,
        };

        var section = new ThinkingSection(thinkingTb, live: true);
        thinkingTb.Tag = section;   // lets FinalizeThinkingSection find it again
        RoundTarget(session.Runtime, turnPanel).Children.Add(section.Root);

        ScrollToBottom(session);

        return thinkingTb;
    }

    public void FinalizeThinkingSection(ChatSession session, TextBlock tb)
    {
        (tb.Tag as ThinkingSection)?.Finish();
        ScrollToBottom(session);
    }

    public StackPanel AddTextSectionToBubble(ChatSession session, StackPanel turnPanel)
    {
        var target = RoundTarget(session.Runtime, turnPanel);

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
        textPanel.Children.Add(new TextBlock
        {
            Text         = "…",
            TextWrapping = TextWrapping.Wrap,
            Foreground   = Brushes.White,
            FontSize     = 14,
            LineHeight   = 22,
        });

        target.Children.Add(textPanel);

        ScrollToBottom(session);

        return textPanel;
    }

    public void UpdateTextSection(StackPanel textPanel, string currentText)
    {
        if (currentText.Length > 6000)
        {
            textPanel.Children.Clear();
            textPanel.Children.Add(new SelectableTextBlock
            {
                Text         = currentText,
                TextWrapping = TextWrapping.Wrap,
                Foreground   = Brushes.White,
                FontSize     = 14,
                LineHeight   = 22,
            });
            return;
        }
        RebuildMarkdownPanel(textPanel, currentText, streaming: true);
    }

    public void FinalizeTextSection(ChatSession session, StackPanel textPanel, string text)
    {
        RebuildMarkdownPanel(textPanel, text, streaming: false);

        var msg = session.Runtime.CurrentTurnMessage;
        if (msg != null)
        {
            if (!string.IsNullOrEmpty(text))
            {
                msg.TextSegments.Add(new DisplayTextSegment
                {
                    Text          = text,
                    Timestamp     = DateTimeOffset.Now,
                    SequenceIndex = session.Runtime.CurrentTurnSequence++,
                });
            }

            if (!string.IsNullOrEmpty(msg.Content))
                msg.Content += "\n\n" + text;
            else
                msg.Content = text;
        }
    }

    private void CancelCurrentLlm()
    {
        try { _current?.Runtime.Cts?.Cancel(); }
        catch { /* already cancelled or disposed — ignore */ }
    }

    public FileToolCard? BeginToolCallWidget(ChatSession session, StackPanel turnPanel, string toolId, string toolName)
    {
        var runtime = session.Runtime;

        if (FileToolCard.TryGetKind(toolName, out var cardKind))
        {
            var card = new FileToolCard(cardKind, live: true);
            RoundTarget(runtime, turnPanel).Children.Add(card.Root);
            ScrollToBottom(session);

            runtime.CurrentToolParamsPanel = card.ParamsSink;
            runtime.CurrentToolResultPanel = card.ProgressSink;

            string cardKey = string.IsNullOrEmpty(toolId) ? $"__noid_{Guid.NewGuid()}" : toolId;
            FileToolCard.Register(cardKey, card);
            runtime.PendingToolResults[cardKey] = result =>
            {
                card.SetResult(result);
                if (ReferenceEquals(runtime.CurrentToolResultPanel, card.ProgressSink))
                    runtime.CurrentToolResultPanel = null;
            };

            return card;
        }

        var paramsPanel = new StackPanel { Spacing = 2 };
        paramsPanel.Children.Add(new TextBlock
        {
            Text         = "…",
            TextWrapping = TextWrapping.Wrap,
            Foreground   = ColMuted,
            FontSize     = 12,
            FontFamily   = new FontFamily("Consolas, Courier New, monospace"),
            LineHeight   = 18,
            Opacity      = 0.6,
        });

        var resultPanel = new StackPanel { Spacing = 2 };
        resultPanel.Children.Add(new TextBlock
        {
            Text         = "Running…",
            TextWrapping = TextWrapping.Wrap,
            Foreground   = ColMuted,
            FontSize     = 12,
            FontFamily   = new FontFamily("Consolas, Courier New, monospace"),
            LineHeight   = 18,
            Opacity      = 0.9,
        });

        var lazyResult    = new LazyToolResultHost(resultPanel);
        bool isExpandedNow = true;   // a live call starts OPEN (folds itself when done)

        var contentPanel = ToolCallSection.BuildBody(
            paramsPanel, resultPanel, isDispatch: toolName == "dispatch_sub_agent");

        var section = new ToolCallSection(toolName, contentPanel,
            expanded =>
            {
                isExpandedNow = expanded;
                if (expanded) lazyResult.OnExpand();
                else          lazyResult.OnCollapse();
            },
            live: true);
        paramsPanel.Tag = section;   // lets SetLastToolParameters find it again
        RoundTarget(runtime, turnPanel).Children.Add(section.Root);

        ScrollToBottom(session);

        runtime.CurrentToolParamsPanel = paramsPanel;
        runtime.CurrentToolResultPanel = resultPanel;

        string key = string.IsNullOrEmpty(toolId) ? $"__noid_{Guid.NewGuid()}" : toolId;
        runtime.PendingToolResults[key] = result =>
        {
            if (ReferenceEquals(runtime.CurrentToolResultPanel, resultPanel))
                runtime.CurrentToolResultPanel = null;

            if (section.WillAutoFold)
            {
                section.Finish(result, () =>
                {
                    lazyResult.SetResult(result, false);
                    isExpandedNow = false;
                });
            }
            else
            {
                lazyResult.SetResult(result, isExpandedNow);
                section.Finish(result, null);
            }
        };

        return null;
    }

    public void AppendToolProgressLine(ChatSession session, string text)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => AppendToolProgressLine(session, text));
            return;
        }
        var panel = session.Runtime.CurrentToolResultPanel;
        if (panel == null) return;

        if (panel.Tag is FileToolCard fileCard)
        {
            fileCard.OnProgress(text);
            return;
        }

        LiveToolTail? tail = null;
        foreach (var child in panel.Children)
        {
            if (child is ScrollViewer sv && sv.Tag is LiveToolTail existing) { tail = existing; break; }
        }

        if (tail == null)
        {
            if (panel.Children.Count > 0 && panel.Children[0] is TextBlock placeholder &&
                placeholder.Text == "Running…")
                placeholder.Text = "Running… (live output)";

            var tb = new SelectableTextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize     = 11.5,
                LineHeight   = 16,
                FontFamily   = new FontFamily("Consolas, Courier New, monospace"),
                Foreground   = new SolidColorBrush(Color.FromRgb(175, 190, 210)),
                Margin       = new Thickness(0, 0, 8, 0),
            };
            var scroll = new ScrollViewer
            {
                MaxHeight                     = 240,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility   = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                Margin                        = new Thickness(0, 4, 0, 0),
                Content                       = tb,
            };
            tail = new LiveToolTail(tb, scroll);
            scroll.Tag = tail;
            panel.Children.Add(scroll);
        }

        tail.Append(text);
        ScrollToBottom(session);
    }

    private sealed class LiveToolTail
    {
        private const int MaxLines = 200;
        private readonly Queue<string> _lines = new();
        private readonly SelectableTextBlock _tb;
        private readonly ScrollViewer _scroll;

        public LiveToolTail(SelectableTextBlock tb, ScrollViewer scroll)
        {
            _tb     = tb;
            _scroll = scroll;
        }

        public void Append(string text)
        {
            foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                _lines.Enqueue(line);
                while (_lines.Count > MaxLines) _lines.Dequeue();
            }
            _tb.Text = string.Join("\n", _lines);
            Dispatcher.UIThread.Post(() => _scroll.ScrollToEnd(), DispatcherPriority.Background);
        }
    }

    public void SetToolResult(ChatSession session, string toolId, string fullResult)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => SetToolResult(session, toolId, fullResult));
            return;
        }

        var pending = session.Runtime.PendingToolResults;
        if (!string.IsNullOrEmpty(toolId) && pending.TryGetValue(toolId, out Action<string>? setter))
        {
            pending.Remove(toolId);
            setter(fullResult);
        }

        ScrollToBottom(session);
    }

    public void MarkToolAbandoned(ChatSession session, string toolId, string reason)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => MarkToolAbandoned(session, toolId, reason));
            return;
        }

        var pending = session.Runtime.PendingToolResults;
        if (!string.IsNullOrEmpty(toolId) && pending.TryGetValue(toolId, out Action<string>? setter))
        {
            pending.Remove(toolId);
            setter($"⚠ Interrupted — {reason}");
        }

        ScrollToBottom(session);
    }

    public void SetLastToolParameters(ChatSession session, string inputJson)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => SetLastToolParameters(session, inputJson));
            return;
        }

        var runtime = session.Runtime;
        if (runtime.CurrentToolParamsPanel == null) return;

        var panel = runtime.CurrentToolParamsPanel;
        runtime.CurrentToolParamsPanel = null;

        if (panel.Tag is FileToolCard) return;

        panel.Children.Clear();
        panel.Children.Add(ToolCallSection.BuildReadableParams(inputJson, (panel.Tag as ToolCallSection)?.ToolName));

        (panel.Tag as ToolCallSection)?.SetParams(inputJson);
    }

    public void RecordThinkingBlock(ChatSession session, string text)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => RecordThinkingBlock(session, text));
            return;
        }
        session.Runtime.CurrentTurnMessage?.ThinkingBlocks.Add(new DisplayThinkingBlock
        {
            Text          = text,
            Timestamp     = DateTimeOffset.Now,
            SequenceIndex = session.Runtime.CurrentTurnSequence++,
        });
    }

    public void BeginToolRecord(ChatSession session, string toolName, string inputJson)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => BeginToolRecord(session, toolName, inputJson));
            return;
        }
        var runtime = session.Runtime;
        if (runtime.CurrentTurnMessage == null) return;
        var record = new DisplayToolCall
        {
            ToolName      = toolName,
            InputJson     = inputJson,
            StartedAt     = DateTimeOffset.Now,
            SequenceIndex = runtime.CurrentTurnSequence++,
        };
        runtime.CurrentTurnMessage.ToolCalls.Add(record);
        runtime.PendingToolRecords.Enqueue(record);
    }

    public void CompleteLastToolRecord(ChatSession session, string result)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => CompleteLastToolRecord(session, result));
            return;
        }
        if (session.Runtime.PendingToolRecords.TryDequeue(out DisplayToolCall? record))
        {
            record.Result      = result;
            record.CompletedAt = DateTimeOffset.Now;
        }
    }

    private static Control BuildBigAssistantBubble(
        StackPanel contentPanel, DateTimeOffset msgTimestamp, Func<string>? getText = null)
    {
        var roleLabel = BuildAssistantHeader(getText ?? (() => ""));

        var bubble = new Border
        {
            CornerRadius        = new CornerRadius(4, 18, 18, 18),
            Padding             = new Thickness(16, 12, 16, 14),
            Background          = BgAiBubble,
            HorizontalAlignment = HorizontalAlignment.Stretch,   // full width, follows window size
            Child               = contentPanel,
        };

        var timestampBlock = new TextBlock
        {
            Text                = msgTimestamp.ToString("HH:mm"),
            FontSize            = 10,
            Foreground          = ColMuted,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        var wrapper = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
        wrapper.Children.Add(roleLabel);
        wrapper.Children.Add(bubble);
        wrapper.Children.Add(timestampBlock);

        return wrapper;
    }
}
