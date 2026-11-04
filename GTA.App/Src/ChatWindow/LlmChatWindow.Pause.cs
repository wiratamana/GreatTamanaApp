using System.Linq;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace GTA.App;

public partial class LlmChatWindow
{
    /// <summary>Handler for the header "⏸ Pause" / "▶ Resume" button — toggles the CURRENTLY ACTIVE session's persisted pau...</summary>
    private void ToggleSessionPause()
    {
        var session = _current;
        if (session == null) return;

        if (session.IsPaused)
            ResumeSession(session);
        else
            PauseSession(session);
    }

    /// <summary>Pauses session: sets its persisted ChatSession.IsPaused flag, mirrors it onto its ChatSessionRuntime (ChatS...</summary>
    internal void PauseSession(ChatSession session)
    {
        session.IsPaused = true;
        session.Runtime.SetUserPaused(true);

        ConversationPersistence.SaveSession(session);
        RefreshSidebarBadge(session);

        if (ReferenceEquals(session, _current))
            RefreshChromeForActiveSession();
    }

    /// <summary>Resumes session — the inverse of PauseSession.</summary>
    internal void ResumeSession(ChatSession session)
    {
        session.IsPaused = false;
        session.Runtime.SetUserPaused(false);

        ConversationPersistence.SaveSession(session);
        RefreshSidebarBadge(session);

        if (ReferenceEquals(session, _current))
            RefreshChromeForActiveSession();
    }

    /// <summary>Pauses taskSession itself (see PauseSession) AND, if one of its steps currently has its own child session's...</summary>
    internal void PauseTaskAndCurrentChild(ChatSession taskSession)
    {
        PauseSession(taskSession);

        string? childId = taskSession.Runtime.TaskCurrentChildSessionId;
        if (childId == null) return;

        var child = _sessions.FirstOrDefault(s => s.Id == childId);
        if (child != null) PauseSession(child);
    }

    /// <summary>Inverse of <see cref="PauseTaskAndCurrentChild"/>.</summary>
    internal void ResumeTaskAndCurrentChild(ChatSession taskSession)
    {
        ResumeSession(taskSession);

        string? childId = taskSession.Runtime.TaskCurrentChildSessionId;
        if (childId == null) return;

        var child = _sessions.FirstOrDefault(s => s.Id == childId);
        if (child != null) ResumeSession(child);
    }

    /// <summary>Updates the header "⏸ Pause" / "⏳ Pausing…" / "▶ Resume" button's label/style/tooltip to reflect the CURREN...</summary>
    internal void RefreshPauseButtonLabel()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(RefreshPauseButtonLabel);
            return;
        }

        if (_pauseButton == null) return;

        var session   = _current;
        bool requested = session?.IsPaused ?? false;
        bool active    = session?.Runtime.IsPauseActive ?? false;

        if (!requested)
        {
            _pauseButton.Content = "⏸ Pause";
            ToolTip.SetTip(_pauseButton,
                "Pause this session — holds any in-flight turn/Task before its NEXT round/step " +
                "(whatever is already running finishes normally), and holds back any pending " +
                "scheduled fire. PERSISTED — survives closing and relaunching the app.");
            UiTheme.StyleButton(_pauseButton, BgAiBubble, ColText,
                hoverBackground: BgHoverItem, pressedBackground: BgActiveItem, borderBrush: ColDivider);
            return;
        }

        if (active)
        {
            _pauseButton.Content = "▶ Resume";
            ToolTip.SetTip(_pauseButton,
                "Resume this session — releases the in-flight turn/Task actually held right now " +
                "before its next round/step, and lets a pending scheduled fire proceed again");
        }
        else
        {
            _pauseButton.Content = "⏳ Pausing…";
            ToolTip.SetTip(_pauseButton,
                "Pause requested — still finishing the current round/tool call, then it will " +
                "hold before the next one starts. Click to cancel the request and resume right away.");
        }

        UiTheme.StyleButton(_pauseButton, ColRetryAmber, Brushes.White);
    }

    /// <summary>Called every ~100ms (see LlmChatWindow.cs constructor's own _roundTickTimer.Tick handler) to keep every "⏸...</summary>
    private void TickPauseVisuals()
    {
        if (_sessions.Count == 0)
        {
            RefreshPauseButtonLabel();
            return;
        }

        foreach (var session in _sessions)
        {
            if (session.IsPaused)
                RefreshSidebarBadge(session);

            session.Runtime.TaskPauseButtonRefresh?.Invoke();
        }

        if (_current != null && _current.IsPaused)
            RefreshChromeForActiveSession();
        else
            RefreshPauseButtonLabel();
    }
}
