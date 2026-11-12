using System;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Media;

namespace GTA.App;

public partial class LlmChatWindow
{
    /// <summary>Cached sidebar row controls for one session, keyed by session id.</summary>
    private sealed class SidebarRowRefs
    {
        public Border     Item      = null!;
        public TextBlock  TitleText = null!;

        /// <summary>The small grey line under the title (kind + time, e.g. "Sub agent · 15:59").</summary>
        public TextBlock  TimeText  = null!;
        public Border     Badge     = null!;
        public TextBlock  BadgeText = null!;

        /// <summary>The "▾/▸" fold-toggle glyph. Every row has one; it only shows while the row has nested child rows (Task steps and/or Sub Agents).</summary>
        public TextBlock? FoldIcon  = null;
    }

    // ── Badge styles ──────────────────────────────────────────────────────────
    // One tinted "pill" look for every sidebar badge: a see-through background and a thin border in the accent
    // colour, with the text in the same accent colour. Readable on the dark sidebar and calmer than solid blocks.

    private sealed class BadgeStyle
    {
        public readonly IBrush Bg;
        public readonly IBrush Border;
        public readonly IBrush Fg;

        public BadgeStyle(Color accent)
        {
            Bg     = new SolidColorBrush(Color.FromArgb(44,  accent.R, accent.G, accent.B));
            Border = new SolidColorBrush(Color.FromArgb(120, accent.R, accent.G, accent.B));
            Fg     = new SolidColorBrush(accent);
        }
    }

    private static readonly BadgeStyle BadgeRunning = new(Color.FromRgb(255, 198, 84));
    private static readonly BadgeStyle BadgeDone    = new(Color.FromRgb(100, 218, 152));
    private static readonly BadgeStyle BadgeWaiting = new(Color.FromRgb(190, 168, 252));
    private static readonly BadgeStyle BadgeWarn    = new(Color.FromRgb(255, 170, 80));
    private static readonly BadgeStyle BadgeError   = new(Color.FromRgb(255, 124, 124));
    private static readonly BadgeStyle BadgeMuted   = new(Color.FromRgb(172, 176, 202));

    private static void ApplyBadge(Border badge, TextBlock badgeText, string text, BadgeStyle style)
    {
        badge.IsVisible       = true;
        badge.Background      = style.Bg;
        badge.BorderBrush     = style.Border;
        badgeText.Text        = text;
        badgeText.FontSize    = 9;
        badgeText.Opacity     = 1.0;
        badgeText.Foreground  = style.Fg;
    }

    // ── Text helpers ──────────────────────────────────────────────────────────

    private static readonly Regex LegacySubAgentTitle =
        new(@"^🤖 Sub Agent \(.*?\): ", RegexOptions.Compiled);

    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Title as shown in the sidebar: always ONE line (task titles can contain line breaks and long runs of
    /// spaces) and without the old "(parent title…)" part of Sub Agent titles — the nesting already shows
    /// who the parent is.
    /// </summary>
    private static string SidebarTitle(ChatSession session)
    {
        string title = session.Title ?? string.Empty;
        if (session.ParentSessionId != null)
            title = LegacySubAgentTitle.Replace(title, "🤖 Sub Agent: ");
        return WhitespaceRun.Replace(title, " ").Trim();
    }

    /// <summary>The grey line under the title: what kind of row this is + when it last changed.</summary>
    private static string SidebarMeta(ChatSession session)
    {
        string time = FormatSessionTime(session.LastModifiedAt);

        if (session.ParentSessionId != null) return "Sub agent  ·  " + time;
        if (session.ParentTaskId    != null) return "Task step  ·  " + time;
        if (session.IsTask)                  return "Task  ·  " + time;
        return time;
    }

    /// <summary>Highlights (or un-highlights) a sidebar row: filled background + accent bar on its left edge.</summary>
    private static void SetRowActive(Border item, bool active)
    {
        item.Background  = active ? ColSideActive : null;
        item.BorderBrush = active ? ColAccent : Brushes.Transparent;
    }

    // ── Badge refresh ─────────────────────────────────────────────────────────

    /// <summary>Repaints the sidebar badge for one session purely from its current Runtime state.</summary>
    private void RefreshSidebarBadge(ChatSession session)
    {
        if (!_sidebarRows.TryGetValue(session.Id, out var refs)) return;

        // Paused: the chat / Task itself, its step chat, or a Sub Agent below it (they are paused through their parent).
        var pauseOwner = ResolvePauseOwner(session);
        bool inPause   = session.IsPaused ||
                         (session.ParentSessionId != null && pauseOwner.IsPaused &&
                          (session.Runtime.IsProcessing || session.TurnInProgress));
        if (inPause)
        {
            bool settled = IsPauseSettled(pauseOwner);   // true = saved, safe to close. false = still finishing the round.
            ApplyBadge(refs.Badge, refs.BadgeText, settled ? "⏸ PAUSED" : "⏳ PAUSING…", BadgeWarn);
            return;
        }

        // A Task step OR a Sub Agent: always shows an explicit RUNNING / DONE / … state.
        if (session.ParentTaskId != null || session.ParentSessionId != null)
        {
            TaskRowBadgeState state;
            if (session.Runtime.IsProcessing)
            {
                state = TaskRowBadgeState.Running;
            }
            else
            {
                var parentTask = session.ParentTaskId == null
                    ? null
                    : _sessions.Find(s => s.Id == session.ParentTaskId);
                TaskUnit? unit = parentTask?.TaskUnits.Find(u => u.ChildSessionId == session.Id);

                if (unit != null && unit.IsSupersededByRestart)
                    state = TaskRowBadgeState.Old;
                else if (unit != null && parentTask != null &&
                         parentTask.Runtime.TaskRestartingIndices.Contains(parentTask.TaskUnits.IndexOf(unit)))
                    state = TaskRowBadgeState.Restarting;
                else if (session.TurnInProgress)
                    state = TaskRowBadgeState.Interrupted; // killed mid-turn: not DONE
                else
                    state = TaskRowBadgeState.Done;
            }

            SetSidebarTaskBadgeState(refs.Badge, refs.BadgeText, state);
            return;
        }

        if (session.ScheduledAt.HasValue)
        {
            ApplyBadge(refs.Badge, refs.BadgeText, "⏳ WAITING", BadgeWaiting);
            return;
        }

        if (session.IsTask && !session.TaskHasRun)
        {
            ApplyBadge(refs.Badge, refs.BadgeText,
                session.TaskScheduledAt.HasValue ? "⏳ WAITING" : "📋 TASK", BadgeWaiting);
            return;
        }

        if (session.Runtime.IsProcessing || session.Runtime.TaskCurrentUnitIndex >= 0)
        {
            refs.Badge.IsVisible      = true;
            refs.Badge.Background     = Brushes.Transparent;
            refs.Badge.BorderBrush    = Brushes.Transparent;
            refs.BadgeText.Text       = "●";
            refs.BadgeText.FontSize   = 14;
            refs.BadgeText.Opacity    = 1.0;
            refs.BadgeText.Foreground = ColProcessing;
        }
        else if (session.TurnInProgress)
        {
            // Not processing now, but the saved state says a turn was still running: closed or killed mid-turn.
            ApplyBadge(refs.Badge, refs.BadgeText, "⚠ INTERRUPTED", BadgeWarn);
        }
        else if (session.Runtime.HasUnseenCompletion)
        {
            ApplyBadge(refs.Badge, refs.BadgeText, "DONE", BadgeDone);
        }
        else
        {
            refs.Badge.IsVisible = false;
        }
    }

    /// <summary>Called every ~100ms by the shared _roundTickTimer (see LlmChatWindow.cs constructor) to gently pulse the op...</summary>
    private void TickSidebarProcessingPulse()
    {
        if (_sessions.Count == 0) return;

        // Smooth pulse between ~0.35 and 1.0 opacity, ~1.2s period.
        double phase   = (Environment.TickCount % 1200) / 1200.0;
        double opacity = 0.35 + 0.65 * (0.5 - 0.5 * Math.Cos(phase * 2 * Math.PI));

        foreach (var session in _sessions)
        {
            if (!(session.Runtime.IsProcessing || session.Runtime.TaskCurrentUnitIndex >= 0)) continue;
            if (_sidebarRows.TryGetValue(session.Id, out var refs) && refs.Badge.IsVisible)
                refs.BadgeText.Opacity = opacity;
        }
    }

    /// <summary>Mark a session as actively processing in the sidebar.</summary>
    private void ShowProcessingBadge(ChatSession session) => RefreshSidebarBadge(session);

    /// <summary>Called when a turn finishes for session (success, cancel, or error).</summary>
    private void ShowDoneBadgeIfInactive(ChatSession session)
    {
        if (!ReferenceEquals(session, _current))
            session.Runtime.HasUnseenCompletion = true;

        RefreshSidebarBadge(session);
    }

    /// <summary>Permanently clears the "DONE" badge for a session — called the moment the user switches into it (see Switch...</summary>
    private void ClearDoneBadge(ChatSession session)
    {
        session.Runtime.HasUnseenCompletion = false;
        RefreshSidebarBadge(session);
    }

    private enum TaskRowBadgeState { Waiting, Running, Done, Skipped, Error, Restarting, Old, Interrupted }

    private static void SetSidebarTaskBadgeState(Border badge, TextBlock badgeText, TaskRowBadgeState state)
    {
        switch (state)
        {
            case TaskRowBadgeState.Waiting:     ApplyBadge(badge, badgeText, "⏳ WAITING",     BadgeWaiting); break;
            case TaskRowBadgeState.Running:     ApplyBadge(badge, badgeText, "▶ RUNNING",      BadgeRunning); break;
            case TaskRowBadgeState.Done:        ApplyBadge(badge, badgeText, "✓ DONE",         BadgeDone);    break;
            case TaskRowBadgeState.Skipped:     ApplyBadge(badge, badgeText, "⏹ SKIPPED",      BadgeMuted);   break;
            case TaskRowBadgeState.Error:       ApplyBadge(badge, badgeText, "✗ ERROR",        BadgeError);   break;
            case TaskRowBadgeState.Interrupted: ApplyBadge(badge, badgeText, "⚠ INTERRUPTED",  BadgeWarn);    break;
            case TaskRowBadgeState.Restarting:  ApplyBadge(badge, badgeText, "⟲ RESTARTING",   BadgeWarn);    break;
            case TaskRowBadgeState.Old:         ApplyBadge(badge, badgeText, "📦 OLD",         BadgeMuted);   break;
        }
    }
}
