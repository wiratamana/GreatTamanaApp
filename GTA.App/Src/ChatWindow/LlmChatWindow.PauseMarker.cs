using System;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace GTA.App;

/// <summary>
/// The "⏸ Currently paused" widget inside the turn's bubble (same look as the "♻ Cache kept warm" note).
///
///   • The turn is saved at a pause  → the widget is added to the bubble ("Currently paused", amber). No session total.
///   • ▶ Resume                      → the SAME widget turns green ("Resumed …") and the turn goes on in the SAME bubble.
///   • Stop while paused             → the widget turns red ("Pause discarded").
///
/// The widget is a <see cref="DisplayPauseMarker"/> in the message, saved with the chat, so it is also there after an app
/// restart. After a restart, the resumed turn re-attaches to the bubble that was rebuilt from the saved chat.
/// </summary>
public partial class LlmChatWindow
{
    /// <summary>The content panel of every built assistant bubble. A resumed turn writes into it again.</summary>
    private static readonly ConditionalWeakTable<ChatMessage, StackPanel> TurnContentPanels = new();

    /// <summary>Repaints a marker's widget (the widget registers itself here when it is built).</summary>
    private static readonly ConditionalWeakTable<DisplayPauseMarker, Action> PauseWidgetUpdaters = new();

    private static bool IsOpen(DisplayPauseMarker m) => m.Outcome.Length == 0;

    // ── The widget ────────────────────────────────────────────────────────────

    internal static Control BuildPauseWidget(DisplayPauseMarker marker)
    {
        var text = new TextBlock
        {
            FontSize     = 11,
            FontFamily   = new FontFamily("Consolas, Courier New, monospace"),
            TextWrapping = TextWrapping.Wrap,
        };

        var note = new Border
        {
            CornerRadius        = new CornerRadius(6),
            BorderThickness     = new Thickness(1),
            Padding             = new Thickness(8, 3, 8, 3),
            Margin              = new Thickness(0, 2, 0, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child               = text,
        };

        void Apply()
        {
            if (IsOpen(marker))
            {
                note.Background  = KaWarnBg;
                note.BorderBrush = KaWarnBorder;
                text.Foreground  = KaWarnFg;
                text.Text        = $"⏸ Currently paused · {marker.PausedAt:HH:mm:ss} · saved, safe to close the app";
                ToolTip.SetTip(note,
                    "This chat stopped at the end of a round and everything is saved (sessions/pause_state.json). " +
                    "Click ▶ Resume and it continues in this same bubble — also after the app was closed and started again.");
            }
            else if (marker.Outcome == "resumed")
            {
                note.Background  = KaOkBg;
                note.BorderBrush = KaOkBorder;
                text.Foreground  = KaOkFg;
                string took = marker.ResumedAt.HasValue
                    ? " · paused for " + FormatPauseSpan(marker.ResumedAt.Value - marker.PausedAt)
                    : string.Empty;
                text.Text = $"▶ Resumed · {marker.ResumedAt?.ToString("HH:mm:ss") ?? "?"}{took}";
                ToolTip.SetTip(note, "The pause is over. The conversation continues below, in this same bubble.");
            }
            else
            {
                note.Background  = KaBadBg;
                note.BorderBrush = KaBadBorder;
                text.Foreground  = KaBadFg;
                text.Text        = $"⏹ Pause discarded · {marker.ResumedAt?.ToString("HH:mm:ss") ?? "?"} · it was stopped, not continued";
                ToolTip.SetTip(note, "The paused work was stopped. It was not continued.");
            }
        }

        Apply();
        PauseWidgetUpdaters.AddOrUpdate(marker, Apply);
        return note;
    }

    private static string FormatPauseSpan(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalHours   >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes}m {t.Seconds}s";
        return $"{(int)t.TotalSeconds}s";
    }

    // ── Create / close ────────────────────────────────────────────────────────

    /// <summary>
    /// Adds the "currently paused" widget to the turn's bubble (UI thread; the loop of this turn is frozen). If no bubble
    /// exists yet (paused before round 1) an empty one is created, so the widget has a place. Call it BEFORE the chat is saved.
    /// </summary>
    private DisplayPauseMarker? BeginPauseMarker(ChatSession session)
    {
        var runtime = session.Runtime;

        if (runtime.CurrentTurnContentPanel == null)
            BeginTurnBubble(session);

        var message = runtime.CurrentTurnMessage;
        var panel   = runtime.CurrentTurnContentPanel;
        if (message == null || panel == null) return null;

        var existing = message.PauseMarkers.FirstOrDefault(IsOpen);
        if (existing != null) return existing;

        var marker = new DisplayPauseMarker
        {
            SequenceIndex = runtime.CurrentTurnSequence++,
            PausedAt      = DateTimeOffset.Now,
        };
        message.PauseMarkers.Add(marker);

        RoundTarget(runtime, panel).Children.Add(BuildPauseWidget(marker));
        ScrollToBottom(session);
        return marker;
    }

    /// <summary>Turns the open pause widget of the session's newest assistant bubble into "resumed" / "stopped".</summary>
    private void ClosePauseMarker(ChatSession session, string outcome)
    {
        var message = session.Messages.LastOrDefault(m => m.Role == "assistant");
        if (message == null) return;

        foreach (var marker in message.PauseMarkers.Where(IsOpen).ToList())
        {
            marker.Outcome   = outcome;
            marker.ResumedAt = DateTimeOffset.Now;
            if (PauseWidgetUpdaters.TryGetValue(marker, out var repaint))
                repaint();
        }
    }

    // ── After an app restart: continue in the SAME bubble ───────────────────

    /// <summary>
    /// A turn is resumed after an app restart. When the newest assistant bubble of the chat carries an OPEN pause widget,
    /// the new rounds are written into that bubble (no second bubble). Returns false when there is no such bubble; then the
    /// normal behaviour (a new bubble) applies. Call it BEFORE <see cref="ClosePauseMarker"/>.
    /// </summary>
    private bool AttachToPausedTurnBubble(ChatSession session)
    {
        var runtime = session.Runtime;
        if (runtime.CurrentTurnContentPanel != null) return false;   // a live turn already has its bubble

        var message = session.Messages.Count > 0 ? session.Messages[^1] : null;
        if (message == null || message.Role != "assistant") return false;
        if (!message.PauseMarkers.Any(IsOpen)) return false;
        if (!TurnContentPanels.TryGetValue(message, out var panel)) return false;

        runtime.ResetTurnScratchState();
        runtime.CurrentTurnMessage      = message;
        runtime.CurrentTurnContentPanel = panel;
        runtime.CurrentTurnSequence     = NextSequenceIndex(message);
        runtime.CurrentTurnUsageTotal   = message.Usage ?? default;   // the per-round totals go on from here
        runtime.TurnUsageBase           = message.Usage ?? default;   // the loop's final total only knows the part after the resume
        return true;
    }

    /// <summary>The first free SequenceIndex of a message (one more than the highest one used).</summary>
    private static int NextSequenceIndex(ChatMessage m)
    {
        int max = -1;
        foreach (var x in m.ThinkingBlocks)     max = Math.Max(max, x.SequenceIndex);
        foreach (var x in m.ToolCalls)          max = Math.Max(max, x.SequenceIndex);
        foreach (var x in m.TextSegments)       max = Math.Max(max, x.SequenceIndex);
        foreach (var x in m.PromptInjections)   max = Math.Max(max, x.SequenceIndex);
        foreach (var x in m.SubAgentDispatches) max = Math.Max(max, x.SequenceIndex);
        foreach (var x in m.Rounds)             max = Math.Max(max, x.SequenceIndex);
        foreach (var x in m.PauseMarkers)       max = Math.Max(max, x.SequenceIndex);
        return max + 1;
    }
}
