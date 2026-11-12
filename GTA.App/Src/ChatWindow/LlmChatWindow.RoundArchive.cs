using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace GTA.App;

/// <summary>
/// Bookkeeping of ONE assistant turn bubble whose OLD rounds are not built as widgets (see LlmChatWindow.RoundArchive.cs).
/// The rounds themselves are never lost: they stay in <see cref="Message"/> (and on disk), and a click on the
/// "earlier rounds" row builds more of them from that data.
/// </summary>
internal sealed class TurnArchive
{
    public ChatMessage Message    = null!;
    public StackPanel  Panel      = null!;     // the turn's content panel (holds the round rows)
    public Border?     Placeholder;            // the "N earlier rounds" row (null while nothing is hidden)
    public TextBlock?  Label;
    public int         Hidden;                 // how many of the OLDEST rounds are not built
    public int         KeepLimit;              // how many round rows may stay built
    public readonly List<Control> RoundRoots = new();   // built round rows, oldest first
}

public partial class LlmChatWindow
{
    // MEMORY. A long agent run is ONE assistant bubble with thousands of rounds. Every round is a tree of
    // Avalonia controls (tool cards, thinking, text, markdown): about 150 controls and ~100 KB per round.
    // Only the newest rounds are kept as widgets; the older ones are rebuilt from the saved data on request.

    /// <summary>Round rows kept as widgets while a turn is RUNNING.</summary>
    private const int LiveKeepRounds = 12;

    /// <summary>Round rows built when a finished turn is shown (opening a chat).</summary>
    private const int ReplayKeepRounds = 10;

    /// <summary>How many hidden rounds one click brings back.</summary>
    private const int ArchiveChunk = 25;

    /// <summary>The most round rows a running turn keeps, even after the user asked for more.</summary>
    private const int MaxKeepRounds = 150;

    /// <summary>Messages built when a chat is opened (older ones: "N earlier messages" row).</summary>
    private const int MessageWindow = 30;
    private const int MessageChunk  = 25;

    // ── Timeline of one assistant message ────────────────────────────────────

    private enum TlKind { Thinking, ToolCall, Text, Injection, SubAgent, Round, Pause }

    private readonly struct TlEvent
    {
        public readonly int    Seq;
        public readonly int    Order;   // insertion order: makes the sort stable for equal Seq
        public readonly TlKind Kind;
        public readonly object Data;
        public TlEvent(int seq, int order, TlKind kind, object data) { Seq = seq; Order = order; Kind = kind; Data = data; }
    }

    /// <summary>All saved events of a message in the order they happened (by SequenceIndex).</summary>
    private static List<TlEvent> BuildTimeline(ChatMessage message)
    {
        var events = new List<TlEvent>(
            message.ThinkingBlocks.Count + message.ToolCalls.Count + message.TextSegments.Count
            + message.PromptInjections.Count + message.SubAgentDispatches.Count + message.Rounds.Count);

        int order = 0;
        foreach (var tb  in message.ThinkingBlocks)     events.Add(new TlEvent(tb.SequenceIndex,  order++, TlKind.Thinking,  tb));
        foreach (var tc  in message.ToolCalls)          events.Add(new TlEvent(tc.SequenceIndex,  order++, TlKind.ToolCall,  tc));
        foreach (var seg in message.TextSegments)       events.Add(new TlEvent(seg.SequenceIndex, order++, TlKind.Text,      seg));
        foreach (var pi  in message.PromptInjections)   events.Add(new TlEvent(pi.SequenceIndex,  order++, TlKind.Injection, pi));
        foreach (var sad in message.SubAgentDispatches) events.Add(new TlEvent(sad.SequenceIndex, order++, TlKind.SubAgent,  sad));
        foreach (var rd  in message.Rounds)             events.Add(new TlEvent(rd.SequenceIndex,  order++, TlKind.Round,     rd));
        foreach (var pm  in message.PauseMarkers)       events.Add(new TlEvent(pm.SequenceIndex,  order++, TlKind.Pause,     pm));

        events.Sort((a, b) =>
        {
            int c = a.Seq.CompareTo(b.Seq);
            return c != 0 ? c : a.Order.CompareTo(b.Order);
        });
        return events;
    }

    /// <summary>Indexes (into the timeline) of the round markers, oldest first.</summary>
    private static List<int> RoundPositions(List<TlEvent> events)
    {
        var pos = new List<int>();
        for (int i = 0; i < events.Count; i++)
            if (events[i].Kind == TlKind.Round) pos.Add(i);
        return pos;
    }

    /// <summary>
    /// Builds the widgets of events[start..end) into <paramref name="container"/>. A round marker adds a round row to
    /// the container; everything after it goes into that row (until the next marker). Used by the session reload
    /// AND by "show earlier rounds", so both look the same.
    /// </summary>
    private void AppendTimeline(List<TlEvent> events, int start, int end, StackPanel container, List<Control>? roundRoots)
    {
        StackPanel target = container;

        for (int i = start; i < end; i++)
        {
            var ev = events[i];
            switch (ev.Kind)
            {
                case TlKind.Round:
                {
                    var row = BuildRoundRow((DisplayRound)ev.Data);
                    container.Children.Add(row.Root);
                    roundRoots?.Add(row.Root);
                    target = row.Items;
                    break;
                }

                case TlKind.Thinking:
                {
                    var tb = (DisplayThinkingBlock)ev.Data;
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

                case TlKind.ToolCall:
                    target.Children.Add(BuildStaticToolCallSection((DisplayToolCall)ev.Data));
                    break;

                case TlKind.Injection:
                    target.Children.Add(BuildPromptInjectionWidget((DisplayPromptInjection)ev.Data));
                    break;

                case TlKind.SubAgent:
                    target.Children.Add(BuildSubAgentDispatchWidget((DisplaySubAgentDispatch)ev.Data));
                    break;

                case TlKind.Pause:
                    target.Children.Add(BuildPauseWidget((DisplayPauseMarker)ev.Data));
                    break;

                case TlKind.Text:
                {
                    var seg = (DisplayTextSegment)ev.Data;
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

    // ── Replay: build only the newest rounds ─────────────────────────────────

    /// <summary>
    /// Fills <paramref name="contentPanel"/> with the saved timeline of <paramref name="message"/>. If the turn has
    /// more than <see cref="ReplayKeepRounds"/> rounds, only the newest ones are built and an "N earlier rounds" row
    /// is put on top.
    /// </summary>
    private void BuildTurnTimeline(ChatMessage message, StackPanel contentPanel)
    {
        var events = BuildTimeline(message);
        var rounds = RoundPositions(events);

        var archive = new TurnArchive
        {
            Message   = message,
            Panel     = contentPanel,
            KeepLimit = ReplayKeepRounds,
        };

        int start = 0;
        if (rounds.Count > ReplayKeepRounds)
        {
            archive.Hidden = rounds.Count - ReplayKeepRounds;
            start          = rounds[archive.Hidden];
        }

        AppendTimeline(events, start, events.Count, contentPanel, archive.RoundRoots);

        if (archive.Hidden > 0)
            UpdateArchivePlaceholder(archive);
    }

    // ── Live: keep only the newest rounds ────────────────────────────────────

    /// <summary>
    /// Called when a new round row was added to the running turn. Takes the oldest round rows out of the visual tree
    /// when there are more than the limit. (A user who scrolled up to read is left alone for a while.)
    /// </summary>
    private void TrimLiveRounds(ChatSession session, StackPanel turnPanel, Control newRoundRoot)
    {
        var runtime = session.Runtime;
        var message = runtime.CurrentTurnMessage;
        if (message == null) return;

        var archive = runtime.CurrentTurnArchive;
        if (archive == null || !ReferenceEquals(archive.Panel, turnPanel))
        {
            archive = new TurnArchive { Message = message, Panel = turnPanel, KeepLimit = LiveKeepRounds };
            runtime.CurrentTurnArchive = archive;
        }

        archive.RoundRoots.Add(newRoundRoot);

        if (archive.RoundRoots.Count <= archive.KeepLimit) return;

        // The user is reading older rounds right now: do not pull them away — unless it gets really big.
        if (ReferenceEquals(session, _current) && !_stickToBottom &&
            archive.RoundRoots.Count <= archive.KeepLimit * 3)
            return;

        bool changed = false;
        while (archive.RoundRoots.Count > archive.KeepLimit)
        {
            var old = archive.RoundRoots[0];
            archive.RoundRoots.RemoveAt(0);
            turnPanel.Children.Remove(old);
            archive.Hidden++;
            changed = true;
        }

        if (changed)
            UpdateArchivePlaceholder(archive);
    }

    // ── The "earlier rounds" row ─────────────────────────────────────────────

    private void UpdateArchivePlaceholder(TurnArchive a)
    {
        if (a.Hidden <= 0)
        {
            if (a.Placeholder != null)
            {
                a.Panel.Children.Remove(a.Placeholder);
                a.Placeholder = null;
                a.Label       = null;
            }
            return;
        }

        if (a.Placeholder == null)
        {
            var label = new TextBlock
            {
                FontSize          = 11,
                FontFamily        = new FontFamily("Consolas, Courier New, monospace"),
                Foreground        = ColMuted,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var ph = new Border
            {
                Background          = BgThinking,
                BorderBrush         = ColDivider,
                BorderThickness     = new Thickness(1),
                CornerRadius        = new CornerRadius(8),
                Padding             = new Thickness(10, 5, 10, 5),
                Margin              = new Thickness(4, 1, 4, 1),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Cursor              = new Cursor(StandardCursorType.Hand),
                Child               = label,
            };
            ToolTip.SetTip(ph,
                "Old rounds of this turn are not kept as widgets, to save memory. Nothing is lost: they are in the " +
                "chat file. Click to build more of them.");
            ph.PointerReleased += (_, e) =>
            {
                if (e.InitialPressMouseButton == MouseButton.Left) RevealEarlierRounds(a);
            };

            a.Placeholder = ph;
            a.Label       = label;
            a.Panel.Children.Insert(0, ph);
        }

        int next = Math.Min(ArchiveChunk, a.Hidden);
        a.Label!.Text =
            $"▸  {a.Hidden:N0} earlier round{(a.Hidden == 1 ? "" : "s")} not shown (saved on disk)  ·  click to show {next} more";
    }

    /// <summary>Builds the next chunk of hidden rounds from the saved message data and puts it under the row.</summary>
    private void RevealEarlierRounds(TurnArchive a)
    {
        if (a.Hidden <= 0) return;

        var events = BuildTimeline(a.Message);
        var rounds = RoundPositions(events);

        int hidden = Math.Min(a.Hidden, rounds.Count);
        if (hidden <= 0) { a.Hidden = 0; UpdateArchivePlaceholder(a); return; }

        int take      = Math.Min(ArchiveChunk, hidden);
        int newHidden = hidden - take;
        int start     = newHidden == 0 ? 0 : rounds[newHidden];
        int end       = hidden < rounds.Count ? rounds[hidden] : events.Count;

        var holder    = new StackPanel();
        var newRoots  = new List<Control>();
        AppendTimeline(events, start, end, holder, newRoots);

        var built = holder.Children.ToList();
        holder.Children.Clear();

        int at = a.Placeholder != null ? a.Panel.Children.IndexOf(a.Placeholder) + 1 : 0;
        a.Panel.Children.InsertRange(at, built);

        a.RoundRoots.InsertRange(0, newRoots);
        a.Hidden    = newHidden;
        a.KeepLimit = Math.Min(MaxKeepRounds, Math.Max(a.KeepLimit, a.RoundRoots.Count));

        UpdateArchivePlaceholder(a);
    }

    // ── Messages of a chat: build only the newest ones ───────────────────────

    /// <summary>Fills the chat panel with the newest messages of the session; older ones come on request.</summary>
    private void BuildMessageWindow(ChatSession session, StackPanel panel)
    {
        int total = session.Messages.Count;
        int start = Math.Max(0, total - MessageWindow);

        for (int i = start; i < total; i++)
            panel.Children.Add(BuildMessageBubble(session, session.Messages[i]));

        if (start > 0)
            AddEarlierMessagesRow(session, panel, start);
    }

    private void AddEarlierMessagesRow(ChatSession session, StackPanel panel, int hiddenCount)
    {
        int hidden = hiddenCount;

        var label = new TextBlock
        {
            FontSize          = 11,
            FontFamily        = new FontFamily("Consolas, Courier New, monospace"),
            Foreground        = ColMuted,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var row = new Border
        {
            Background          = BgThinking,
            BorderBrush         = ColDivider,
            BorderThickness     = new Thickness(1),
            CornerRadius        = new CornerRadius(8),
            Padding             = new Thickness(10, 5, 10, 5),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Cursor              = new Cursor(StandardCursorType.Hand),
            Child               = label,
        };
        ToolTip.SetTip(row, "Older messages are not kept as widgets, to save memory. Click to show more.");

        void SetText() =>
            label.Text = $"▸  {hidden:N0} earlier message{(hidden == 1 ? "" : "s")} not shown  ·  click to show {Math.Min(MessageChunk, hidden)} more";
        SetText();

        row.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left) return;
            if (hidden <= 0 || !session.IsFullyLoaded) return;

            int from = Math.Max(0, hidden - MessageChunk);
            var built = new List<Control>();
            for (int i = from; i < hidden && i < session.Messages.Count; i++)
                built.Add(BuildMessageBubble(session, session.Messages[i]));

            int at = panel.Children.IndexOf(row) + 1;
            panel.Children.InsertRange(at, built);

            hidden = from;
            if (hidden <= 0) panel.Children.Remove(row);
            else             SetText();
        };

        panel.Children.Insert(0, row);
    }
}
