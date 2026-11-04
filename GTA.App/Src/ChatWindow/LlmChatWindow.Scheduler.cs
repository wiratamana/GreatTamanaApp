using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace GTA.App;

public partial class LlmChatWindow
{
    // ── Open the scheduler window ─────────────────────────────────────────────

    /// <summary>Handler for the sidebar "⏰ Scheduler" button — opens a fresh SchedulerPromptWindow where the user types a p...</summary>
    private void OpenSchedulerWindow()
    {
        var win = new SchedulerPromptWindow(this);
        win.Show(this);
    }

    // ── Create a scheduled chat ────────────────────────────────────────────────

    /// <summary>Creates a brand-new ChatSession that is a pending scheduled prompt (see ChatSession.ScheduledAt), inserts i...</summary>
    internal void CreateScheduledChat(
        string                 prompt,
        DateTimeOffset         when,
        List<string>?          attachments  = null,
        IEnumerable<string>?   preloadTools = null)
    {
        string title = prompt.Length > 40 ? prompt[..40] + "…" : prompt;

        var session = new ChatSession
        {
            Title                = $"⏰ {title}",
            IsFullyLoaded        = true,
            ScheduledAt          = when,
            ScheduledPrompt      = prompt,
            ScheduledAttachments = attachments != null ? new List<string>(attachments) : new List<string>(),
        };

        if (preloadTools != null)
            foreach (var name in preloadTools)
                session.Runtime.PreloadedToolNames.Add(name);

        _sessions.Insert(0, session);
        var item = AddSessionListItem(session, prependToTop: true);
        SwitchSession(session, item);

        ConversationPersistence.SaveSession(session);
    }

    // ── Tick-driven check (called every ~100ms — see LlmChatWindow.cs) ───────

    /// <summary>Fires any session whose ChatSession.ScheduledAt has been reached.</summary>
    private void CheckScheduledPrompts()
    {
        if (_sessions.Count == 0) return;

        var now = DateTimeOffset.Now;

        foreach (var session in _sessions.ToArray())
        {
            if (session.ScheduledAt.HasValue &&
                session.ScheduledAt.Value <= now &&
                !session.Runtime.IsProcessing &&
                !session.IsPaused) // "⏸ Pause" — see LlmChatWindow.Pause.cs
            {
                FireScheduledPrompt(session);
            }
        }
    }

    // ── Fire (automatic or "Start Now") ───────────────────────────────────────

    /// <summary>Fires session's scheduled prompt right now — either because ChatSession.ScheduledAt was reached (called fro...</summary>
    private void FireScheduledPrompt(ChatSession session)
    {
        if (session.Runtime.IsProcessing) return; // already fired/running

        string? prompt = session.ScheduledPrompt;

        session.ScheduledAt = null; // pending → fired, immediately (no race)
        session.Runtime.ScheduledCountdownLabel = null;
        RefreshSidebarBadge(session);

        if (string.IsNullOrWhiteSpace(prompt))
        {
            // Nothing to send (shouldn't normally happen) — just persist the
            // now-cleared schedule so it doesn't come back on next restart.
            ConversationPersistence.SaveSession(session);
            return;
        }

        var existingAttachments = session.ScheduledAttachments
            .Where(File.Exists)
            .ToList();

        _ = SendTextToSession(session, prompt, existingAttachments, null);
    }

    // ── Cancel ─────────────────────────────────────────────────────────────────

    /// <summary>Cancels a still-pending scheduled prompt — the "✕ Cancel" button on BuildScheduledPlaceholder.</summary>
    private void CancelScheduledChat(ChatSession session)
    {
        session.ScheduledAt     = null;
        session.ScheduledPrompt = null;
        session.Runtime.ScheduledCountdownLabel = null;

        if (_sidebarRows.TryGetValue(session.Id, out var refs))
        {
            _sessionListPanel.Children.Remove(refs.Item);
            _sidebarRows.Remove(session.Id);
        }
        _sessions.Remove(session);
        ConversationPersistence.DeleteSession(session.Id);

        if (!ReferenceEquals(_current, session)) return;

        _current           = null;
        _activeSessionItem = null;

        if (_sessions.Count > 0 && _sidebarRows.TryGetValue(_sessions[0].Id, out var firstRefs))
            SwitchSession(_sessions[0], firstRefs.Item);
        else
            CreateNewChat();
    }

    // ── Live countdown (ticked every ~100ms — see LlmChatWindow.cs) ──────────

    /// <summary>Refreshes the ACTIVE session's live countdown label (if it currently has one — i.e.</summary>
    private void UpdateScheduledCountdownLabel()
    {
        var session = _current;
        if (session == null) return;
        if (!session.ScheduledAt.HasValue) return;

        var label = session.Runtime.ScheduledCountdownLabel;
        if (label == null) return;

        SetCountdownText(label, session.ScheduledAt.Value);
    }

    private static void SetCountdownText(TextBlock label, DateTimeOffset target)
    {
        var remaining = target - DateTimeOffset.Now;
        label.Text = remaining <= TimeSpan.Zero
            ? "⏳ Time reached — starting…"
            : $"⏳ Fires in {FormatCountdown(remaining)}";
    }

    private static string FormatCountdown(TimeSpan ts)
    {
        if (ts.TotalDays >= 1)  return $"{(int)ts.TotalDays}d {ts.Hours}h {ts.Minutes}m";
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h {ts.Minutes}m {ts.Seconds}s";
        if (ts.TotalMinutes >= 1) return $"{(int)ts.TotalMinutes}m {ts.Seconds}s";
        return $"{ts.Seconds}s";
    }

    // ── Placeholder UI ─────────────────────────────────────────────────────────

    /// <summary>Build the "⏰ Scheduled Prompt" placeholder shown in the main chat area for a session with ChatSession.Sched...</summary>
    private Control BuildScheduledPlaceholder(ChatSession session)
    {
        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
            Margin              = new Thickness(0, 50, 0, 0),
            Spacing             = 10,
            MaxWidth            = 560,
        };

        panel.Children.Add(new TextBlock
        {
            Text                = "⏰",
            FontSize            = 48,
            Foreground          = ColScheduledAccent,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        panel.Children.Add(new TextBlock
        {
            Text                = "Scheduled Prompt",
            FontSize            = 22,
            FontWeight          = FontWeight.SemiBold,
            Foreground          = ColText,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        string whenText = session.ScheduledAt.HasValue
            ? session.ScheduledAt.Value.ToString("dddd, MMM d  •  HH:mm")
            : "—";

        panel.Children.Add(new TextBlock
        {
            Text                = $"Fires at {whenText}",
            FontSize            = 13,
            Foreground          = ColMuted,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        var countdownLabel = new TextBlock
        {
            FontSize            = 13,
            FontWeight          = FontWeight.SemiBold,
            Foreground          = ColScheduledAccent,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin              = new Thickness(0, 0, 0, 4),
        };
        if (session.ScheduledAt.HasValue)
            SetCountdownText(countdownLabel, session.ScheduledAt.Value);
        session.Runtime.ScheduledCountdownLabel = countdownLabel;
        panel.Children.Add(countdownLabel);

        var promptBox = new Border
        {
            Background      = BgAiBubble,
            BorderBrush     = ColDivider,
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(8),
            Padding         = new Thickness(16, 12, 16, 12),
            Margin          = new Thickness(0, 6, 0, 6),
            Child           = new SelectableTextBlock
            {
                Text         = session.ScheduledPrompt ?? string.Empty,
                TextWrapping = TextWrapping.Wrap,
                Foreground   = ColText,
                FontSize     = 13,
            },
        };
        panel.Children.Add(promptBox);

        if (session.ScheduledAttachments.Count > 0 || session.Runtime.PreloadedToolNames.Count > 0)
        {
            var infoRow = new StackPanel
            {
                Orientation         = Orientation.Horizontal,
                Spacing             = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin              = new Thickness(0, 0, 0, 4),
            };

            if (session.ScheduledAttachments.Count > 0)
            {
                string names = string.Join(", ", session.ScheduledAttachments.Select(System.IO.Path.GetFileName));
                var chip = new Border
                {
                    Background   = BgCodeBlock,
                    BorderBrush  = ColDivider,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding      = new Thickness(8, 3, 8, 3),
                    Child        = new TextBlock
                    {
                        Text     = $"📎 {session.ScheduledAttachments.Count} file(s)",
                        FontSize = 11,
                        Foreground = ColMuted,
                    },
                };
                ToolTip.SetTip(chip, names);
                infoRow.Children.Add(chip);
            }

            if (session.Runtime.PreloadedToolNames.Count > 0)
            {
                var chip = new Border
                {
                    Background   = BgCodeBlock,
                    BorderBrush  = ColDivider,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding      = new Thickness(8, 3, 8, 3),
                    Child        = new TextBlock
                    {
                        Text     = $"🧩 {session.Runtime.PreloadedToolNames.Count} tool(s) preloaded",
                        FontSize = 11,
                        Foreground = ColMuted,
                    },
                };
                ToolTip.SetTip(chip, string.Join(", ", session.Runtime.PreloadedToolNames));
                infoRow.Children.Add(chip);
            }

            panel.Children.Add(infoRow);
        }

        var btnRow = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            Spacing             = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var startNowBtn = new Button
        {
            Content    = "▶  Start Now",
            FontSize   = 13,
            FontWeight = FontWeight.SemiBold,
            Padding    = new Thickness(16, 8, 16, 8),
        };
        UiTheme.StyleButton(startNowBtn, ColAccent, Brushes.White);
        ToolTip.SetTip(startNowBtn, "Send this prompt right now, without waiting for the scheduled time");
        startNowBtn.Click += (_, _) => FireScheduledPrompt(session);

        var cancelBtn = new Button
        {
            Content    = "✕  Cancel",
            FontSize   = 13,
            FontWeight = FontWeight.SemiBold,
            Padding    = new Thickness(16, 8, 16, 8),
        };
        UiTheme.StyleButton(cancelBtn, ColRunningRed, Brushes.White);
        ToolTip.SetTip(cancelBtn, "Cancel this scheduled prompt and remove this chat");
        cancelBtn.Click += (_, _) => CancelScheduledChat(session);

        btnRow.Children.Add(startNowBtn);
        btnRow.Children.Add(cancelBtn);
        panel.Children.Add(btnRow);

        return panel;
    }
}
