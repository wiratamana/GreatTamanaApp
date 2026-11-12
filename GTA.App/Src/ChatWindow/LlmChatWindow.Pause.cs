using System.Linq;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace GTA.App;

// UI side of ⏸ Pause / ▶ Resume. The logic (what is saved, how it is continued) is in LlmChatWindow.PauseDump.cs.
public partial class LlmChatWindow
{
    // ── Flags (no saving here: the loop is still running while a pause is only REQUESTED) ──

    /// <summary>Requests a pause of one chat turn. It stops at the end of its current round. See LlmChatWindow.PauseDump.cs.</summary>
    internal void PauseSession(ChatSession session)
    {
        session.IsPaused = true;
        session.Runtime.SetUserPaused(true);

        // No text in the chat here: the button shows "⏳ Pausing…", and the bubble gets its "⏸ Currently paused"
        // widget when everything is saved (see LlmChatWindow.PauseMarker.cs).
        RefreshSidebarBadge(session);
        if (ReferenceEquals(session, _current))
            RefreshChromeForActiveSession();
    }

    /// <summary>Releases a paused chat turn (the loop continues; its Sub Agents continue too).</summary>
    internal void ResumeSession(ChatSession session)
    {
        // The pause widget of the bubble (and of every running Sub Agent's bubble) turns green: "Resumed".
        foreach (string twinId in session.Runtime.DispatchTracker.RunningTwinIds())
        {
            var twin = _sessions.FirstOrDefault(x => x.Id == twinId);
            if (twin != null) ClosePauseMarker(twin, "resumed");
        }
        ClosePauseMarker(session, "resumed");

        session.IsPaused = false;
        session.Runtime.SetUserPaused(false);
        session.Runtime.PauseDumped = false;

        RefreshSidebarBadge(session);
        if (ReferenceEquals(session, _current))
            RefreshChromeForActiveSession();
    }

    /// <summary>Pauses taskSession itself AND the step chat it runs right now (with its Sub Agents).</summary>
    internal void PauseTaskAndCurrentChild(ChatSession taskSession)
    {
        PauseSession(taskSession);

        var child = CurrentChildOf(taskSession);
        if (child != null)
        {
            child.IsPaused = true;
            child.Runtime.SetUserPaused(true);
            RefreshSidebarBadge(child);
        }
    }

    /// <summary>Inverse of <see cref="PauseTaskAndCurrentChild"/>.</summary>
    internal void ResumeTaskAndCurrentChild(ChatSession taskSession)
    {
        ResumeSession(taskSession);

        var child = CurrentChildOf(taskSession);
        if (child != null)
        {
            ResumeSession(child);
            child.Runtime.PauseDumped = false;
        }
    }

    // ── The per-session button (in the input row, in the place of ⑂ Fork) ──────

    /// <summary>Click of the ⏸ / ▶ button next to the input box.</summary>
    private void OnSessionPauseClicked()
    {
        var session = _current;
        if (session == null) return;
        if (session.ParentSessionId != null) return;   // a Sub Agent is paused / resumed through its parent only

        var owner = ResolvePauseOwner(session);

        if (!owner.IsPaused)
            RequestPause(owner);
        else if (IsPauseSettled(owner))
            ResumeOwner(owner);
        // else: "Pausing…" — the button is disabled and does nothing.

        RefreshPauseButtonLabel();
    }

    // ── Button looks ─────────────────────────────────────────────────────────

    // Cached so the 100 ms tick does not restyle (and repaint) a button that did not change.
    private int _sessionPauseState = -1;   // 0 pause, 1 pausing, 2 resume, 3 disabled (Sub Agent)
    private int _globalPauseState  = -1;   // 0 pause (disabled), 1 pause, 2 pausing, 3 resume

    /// <summary>Repaints the global ⏸ button (top right) and the per-session ⏸ / ▶ button (in the place of ⑂ Fork).</summary>
    internal void RefreshPauseButtonLabel()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(RefreshPauseButtonLabel);
            return;
        }

        RefreshGlobalPauseButton();
        RefreshSessionPauseButton();
    }

    private void RefreshGlobalPauseButton()
    {
        if (_globalPauseButton == null) return;

        var ui = ComputeGlobalPauseUi();
        int state = (int)ui;
        if (state == _globalPauseState) return;
        _globalPauseState = state;

        switch (ui)
        {
            case GlobalPauseUi.PauseDisabled:
                _globalPauseButton.Content   = "⏸ Pause";
                _globalPauseButton.IsEnabled = false;
                ToolTip.SetTip(_globalPauseButton, "Pause everything that is running. Nothing is running right now.");
                UiTheme.StyleButton(_globalPauseButton, BgAiBubble, ColText,
                    hoverBackground: BgHoverItem, pressedBackground: BgActiveItem, borderBrush: ColDivider,
                    disabledBackground: ColDisabled, disabledForeground: ColMuted);
                break;

            case GlobalPauseUi.PauseEnabled:
                _globalPauseButton.Content   = "⏸ Pause";
                _globalPauseButton.IsEnabled = true;
                ToolTip.SetTip(_globalPauseButton,
                    "Pause ALL running chats and Tasks (and their Sub Agents). Each one stops at the end of its current round. " +
                    "When all have stopped, everything is saved and you can close the app, recompile, and start it again. " +
                    "Then click ▶ Resume. (Do it within 5 minutes to keep the prompt cache warm.)");
                UiTheme.StyleButton(_globalPauseButton, BgAiBubble, ColText,
                    hoverBackground: BgHoverItem, pressedBackground: BgActiveItem, borderBrush: ColDivider,
                    disabledBackground: ColDisabled, disabledForeground: ColMuted);
                break;

            case GlobalPauseUi.Pausing:
                _globalPauseButton.Content   = "⏳ Pausing…";
                _globalPauseButton.IsEnabled = false;
                ToolTip.SetTip(_globalPauseButton,
                    "Pause requested. Waiting until every running chat / Task / Sub Agent reaches the end of its round. " +
                    "Do NOT close the app yet.");
                UiTheme.StyleButton(_globalPauseButton, ColRetryAmber, Brushes.White,
                    disabledBackground: ColRetryAmber, disabledForeground: Brushes.White);
                break;

            case GlobalPauseUi.Resume:
                _globalPauseButton.Content   = "▶ Resume";
                _globalPauseButton.IsEnabled = true;
                ToolTip.SetTip(_globalPauseButton,
                    "Everything is paused and saved (sessions/pause_state.json). It is safe to close the app now. " +
                    "Click to continue all paused chats and Tasks — also after the app was closed and started again.");
                UiTheme.StyleButton(_globalPauseButton, ColDoneBadgeBg, ColDoneBadgeFg);
                break;
        }
    }

    private void RefreshSessionPauseButton()
    {
        if (_pauseButton == null) return;

        var session = _current;
        bool running = session != null &&
                       (IsSessionRunning(session) || ResolvePauseOwner(session).Runtime.RestoredPause != null);

        // Running: the pause button takes the place of ⑂ Fork. Not running: ⑂ Fork is back.
        _pauseButton.IsVisible    = running;
        _forkSendButton.IsVisible = !running;
        if (!running || session == null)
        {
            _sessionPauseState = -1;
            return;
        }

        bool isSubAgent = session.ParentSessionId != null;
        var  owner      = ResolvePauseOwner(session);
        bool requested  = owner.IsPaused;
        bool settled    = requested && IsPauseSettled(owner);

        int state = isSubAgent ? 3 : (!requested ? 0 : (settled ? 2 : 1));
        if (state == _sessionPauseState) return;
        _sessionPauseState = state;

        switch (state)
        {
            case 0:
                _pauseButton.Content   = "⏸";
                _pauseButton.IsEnabled = true;
                ToolTip.SetTip(_pauseButton,
                    owner.IsTask || !ReferenceEquals(owner, session)
                        ? "Pause this Task (all its steps and Sub Agents). It stops at the end of the current round, then everything is saved."
                        : "Pause this chat (and its Sub Agents). It stops at the end of the current round, then everything is saved.");
                UiTheme.StyleButton(_pauseButton, BgActiveItem, Brushes.White,
                    hoverBackground: BgHoverItem, pressedBackground: BgAiBubble,
                    disabledBackground: ColDisabled, disabledForeground: ColMuted);
                break;

            case 1:
                _pauseButton.Content   = "⏳";
                _pauseButton.IsEnabled = false;
                ToolTip.SetTip(_pauseButton,
                    "Pausing… waiting for the end of the current round (and for the Sub Agents). Do NOT close the app yet.");
                UiTheme.StyleButton(_pauseButton, ColRetryAmber, Brushes.White,
                    disabledBackground: ColRetryAmber, disabledForeground: Brushes.White);
                break;

            case 2:
                _pauseButton.Content   = "▶";
                _pauseButton.IsEnabled = true;
                ToolTip.SetTip(_pauseButton,
                    "Paused and saved. It is safe to close the app. Click to continue.");
                UiTheme.StyleButton(_pauseButton, ColDoneBadgeBg, ColDoneBadgeFg,
                    disabledBackground: ColDisabled, disabledForeground: ColMuted);
                break;

            default:
                _pauseButton.Content   = "⏸";
                _pauseButton.IsEnabled = false;
                ToolTip.SetTip(_pauseButton,
                    "A Sub Agent can not be paused or resumed on its own. Pause / resume its parent chat or Task.");
                UiTheme.StyleButton(_pauseButton, ColDisabled, ColMuted,
                    disabledBackground: ColDisabled, disabledForeground: ColMuted);
                break;
        }
    }

    /// <summary>Called every ~100ms by the shared tick timer: saves what reached its fence and keeps all pause visuals current.</summary>
    private void TickPauseVisuals()
    {
        if (_sessions.Count == 0)
        {
            RefreshPauseButtonLabel();
            return;
        }

        PollPauseDumps();

        foreach (var session in _sessions)
        {
            if (session.IsPaused)
                RefreshSidebarBadge(session);

            // A Sub Agent chat has no pause flag of its own: it is paused THROUGH its parent. Its badge must follow the
            // state of the parent chain: RUNNING → ⏳ PAUSING… (parent asked, this one still works) → ⏸ PAUSED (all frozen)
            // → RUNNING again after the resume. Repaint it only when that state changes (RUNNING has a pulse animation).
            if (session.ParentSessionId != null)
            {
                bool alive = session.Runtime.IsProcessing || session.TurnInProgress;
                int shown = 0;
                if (alive)
                {
                    var owner = ResolvePauseOwner(session);
                    if (owner.IsPaused) shown = IsPauseSettled(owner) ? 2 : 1;
                }

                if (!_subAgentBadgeState.TryGetValue(session.Id, out int old) || old != shown)
                {
                    _subAgentBadgeState[session.Id] = shown;
                    RefreshSidebarBadge(session);
                }
            }

            session.Runtime.TaskPauseButtonRefresh?.Invoke();
        }

        // The chat on screen may be a step / Sub Agent of the Task that was just paused or resumed: repaint the input row.
        bool pausedNow = _current != null && ResolvePauseOwner(_current).IsPaused;
        if (pausedNow != _lastChromePaused)
        {
            _lastChromePaused = pausedNow;
            RefreshChromeForActiveSession();
        }

        RefreshPauseButtonLabel();
    }

    private bool _lastChromePaused;

    /// <summary>Last badge state painted for each Sub Agent chat: 0 normal, 1 pausing, 2 paused (see TickPauseVisuals).</summary>
    private readonly System.Collections.Generic.Dictionary<string, int> _subAgentBadgeState = new();
}
