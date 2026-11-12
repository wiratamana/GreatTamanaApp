using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using GTA.Core;
using GTA.Tools;

namespace GTA.App;

/// <summary>What a resumed chat turn needs on top of its saved history (see AgenticLoop.RunAsync).</summary>
internal sealed class TurnResume
{
    public int                  NextRound { get; init; } = 1;
    public DispatchResumeState? Dispatch  { get; init; }
}

/// <summary>
/// ⏸ Pause / ▶ Resume with a save file.
///
/// The idea: "stop at the end of the round, write everything to disk, then the app may be closed, recompiled and started
/// again; ▶ Resume continues exactly there" — while the prompt cache (5 minutes) is hopefully still warm.
///
///   • Pause  = a request. Every running chat turn / Task (and every Sub Agent below it) keeps running until the END of its
///              current round. It never starts the next round. ("Pausing")
///   • Frozen = the loop holds at the end of the round. Nothing changes the history any more. The window saves the chat
///              (session.json) and writes a <see cref="PauseEntry"/> into sessions/pause_state.json. ("Paused")
///   • Resume = (same app) the loop is released. (after a restart) the work is started again from the saved entry.
///
/// What can be paused: a chat turn, or a whole Task (it holds between two steps, or in the middle of a step's turn).
/// A Sub Agent can NOT be paused or resumed on its own: it is paused / resumed together with its parent chat or Task.
/// </summary>
public partial class LlmChatWindow
{
    // ── State ─────────────────────────────────────────────────────────────────

    /// <summary>The global ⏸ button was pressed and not resumed yet: everything that runs (or starts) is paused.</summary>
    private bool _globalPauseRequested;

    /// <summary>Everything that is saved as paused right now (same content as sessions/pause_state.json). Key = session id.</summary>
    private readonly Dictionary<string, PauseEntry> _pauseEntries = new();

    private enum GlobalPauseUi { PauseDisabled, PauseEnabled, Pausing, Resume }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>True while a chat turn or a Task loop of session is alive.</summary>
    internal static bool IsSessionRunning(ChatSession s) =>
        s.Runtime.IsProcessing || s.Runtime.TaskLoopActive || s.Runtime.TaskCurrentUnitIndex >= 0;

    /// <summary>
    /// True when s is the step chat of a Task and that Task owns it right now: the Task runs exactly this step, or the Task
    /// was loaded from the pause file with exactly this step paused in the middle of its turn.
    /// </summary>
    private bool IsControlledChild(ChatSession s)
    {
        if (s.ParentTaskId == null) return false;
        return _sessions.Any(t => t.Id == s.ParentTaskId &&
                                  ((t.Runtime.TaskLoopActive && t.Runtime.TaskCurrentChildSessionId == s.Id) ||
                                   t.Runtime.RestoredPause?.Child?.SessionId == s.Id));
    }

    /// <summary>
    /// Who really owns the pause of session: a Sub Agent twin → its parent; a step chat of a running Task → the Task.
    /// The user can only pause / resume the owner.
    /// </summary>
    private ChatSession ResolvePauseOwner(ChatSession s)
    {
        var cur  = s;
        var seen = new HashSet<string> { s.Id };

        while (true)
        {
            ChatSession? next = null;

            if (cur.ParentSessionId != null)
                next = _sessions.FirstOrDefault(x => x.Id == cur.ParentSessionId);
            else if (IsControlledChild(cur))
                next = _sessions.FirstOrDefault(x => x.Id == cur.ParentTaskId);

            if (next == null || !seen.Add(next.Id)) return cur;
            cur = next;
        }
    }

    /// <summary>Every session the user can pause / resume: running chats and Tasks, and the ones loaded as paused from the pause file.</summary>
    private List<ChatSession> GetPauseOwners() =>
        _sessions.Where(s => s.ParentSessionId == null &&
                             !IsControlledChild(s) &&
                             (IsSessionRunning(s) || s.Runtime.RestoredPause != null))
                 .ToList();

    private ChatSession? CurrentChildOf(ChatSession task)
    {
        string? childId = task.Runtime.TaskCurrentChildSessionId;
        return childId == null ? null : _sessions.FirstOrDefault(x => x.Id == childId);
    }

    /// <summary>A turn is frozen when its main loop holds at the end of a round, or when it waits for Sub Agents that ALL hold.</summary>
    private static bool IsTurnSettled(ChatSession s) =>
        s.Runtime.AtMainFence || s.Runtime.DispatchTracker.IsFrozen;

    /// <summary>
    /// True when the pause of owner is complete: nothing runs and nothing changes any more, so it is safe to save and to close the app.
    /// </summary>
    internal bool IsPauseSettled(ChatSession owner)
    {
        var r = owner.Runtime;
        if (r.RestoredPause != null) return true;

        if (owner.IsTask && r.TaskLoopActive)
        {
            if (r.TaskPausedAtIndex >= 0) return true;               // holds between two steps
            var child = CurrentChildOf(owner);
            return child != null && IsTurnSettled(child);            // holds in the middle of a step's turn
        }

        return IsTurnSettled(owner);
    }

    // ── Request / release ─────────────────────────────────────────────────────

    /// <summary>Asks owner to pause. Does nothing when it does not run or is already paused.</summary>
    internal void RequestPause(ChatSession owner)
    {
        if (owner.IsPaused || !IsSessionRunning(owner)) return;

        if (owner.IsTask) PauseTaskAndCurrentChild(owner);
        else              PauseSession(owner);

        ConversationPersistence.AppendJournal(owner.Id, "PAUSE REQUESTED", "User asked to pause at the end of the round.");
    }

    /// <summary>Releases owner: a running loop continues; a session loaded from the pause file is started again from its entry.</summary>
    internal void ResumeOwner(ChatSession owner)
    {
        var restored = owner.Runtime.RestoredPause;

        if (restored != null)
        {
            StartFromRestored(owner, restored);
        }
        else
        {
            if (owner.IsTask) ResumeTaskAndCurrentChild(owner);
            else              ResumeSession(owner);
        }

        ConversationPersistence.AppendJournal(owner.Id, "RESUMED", restored != null
            ? "Continued from the pause file."
            : "Released in the running app.");
        // A restarted entry reads its Sub Agent file itself (see ResumeTurnFromEntryAsync); do not delete it from under it.
        RemovePauseEntry(owner.Id, deleteDispatchFiles: restored == null);
    }

    // ── Global ⏸ / ▶ button ──────────────────────────────────────────────────

    private GlobalPauseUi ComputeGlobalPauseUi()
    {
        var owners = GetPauseOwners();

        if (_globalPauseRequested)
        {
            bool settledAll = owners.All(o => !IsSessionRunning(o) || (o.IsPaused && (o.Runtime.PauseDumped || o.Runtime.RestoredPause != null)));
            if (!settledAll) return GlobalPauseUi.Pausing;
            return owners.Any(o => o.IsPaused) ? GlobalPauseUi.Resume : GlobalPauseUi.PauseDisabled;
        }

        bool anyRunningUnpaused = owners.Any(o => !o.IsPaused && IsSessionRunning(o));
        if (anyRunningUnpaused) return GlobalPauseUi.PauseEnabled;

        bool anyPausedSaved = _pauseEntries.Count > 0 || owners.Any(o => o.Runtime.RestoredPause != null);
        return anyPausedSaved ? GlobalPauseUi.Resume : GlobalPauseUi.PauseDisabled;
    }

    private void OnGlobalPauseClicked()
    {
        switch (ComputeGlobalPauseUi())
        {
            case GlobalPauseUi.PauseEnabled: RequestGlobalPause(); break;
            case GlobalPauseUi.Resume:       ResumeAll();          break;
            // Pausing / disabled: the button does nothing.
        }
        RefreshPauseButtonLabel();
    }

    /// <summary>Pause EVERYTHING that runs: every chat turn and every Task (with their Sub Agents).</summary>
    private void RequestGlobalPause()
    {
        _globalPauseRequested = true;
        int n = 0;
        foreach (var owner in GetPauseOwners())
        {
            if (owner.IsPaused || !IsSessionRunning(owner)) continue;
            RequestPause(owner);
            n++;
        }
        AppendLog($"[Pause] Global pause requested for {n} running chat(s)/Task(s).");
    }

    /// <summary>▶ Resume EVERYTHING that is paused (in the running app, or loaded from the pause file).</summary>
    private void ResumeAll()
    {
        _globalPauseRequested = false;
        foreach (var owner in GetPauseOwners().Where(o => o.IsPaused).ToList())
            ResumeOwner(owner);
        AppendLog("[Pause] Resumed.");
    }

    // ── The 100 ms tick: save what has reached its fence ─────────────────────

    private void PollPauseDumps()
    {
        if (_sessions.Count == 0) return;

        bool fileChanged = false;

        foreach (var s in _sessions.ToArray())
        {
            if (s.ParentSessionId != null) continue;      // Sub Agent twin: owned by its parent
            if (IsControlledChild(s))      continue;      // Task step: owned by its Task

            var r = s.Runtime;

            // Global pause is on: whatever started running in the meantime is paused as well.
            if (_globalPauseRequested && !s.IsPaused && IsSessionRunning(s))
            {
                RequestPause(s);
                continue;
            }

            if (!s.IsPaused) continue;
            if (r.RestoredPause != null) continue;        // loaded from the file: nothing runs

            if (!IsSessionRunning(s))
            {
                // Paused flag but nothing runs any more (the turn ended by itself before it reached a fence).
                ClearPauseState(s);
                continue;
            }

            if (r.PauseDumped)
            {
                fileChanged |= RefreshDumpedInjections(s);
                continue;
            }

            if (!IsPauseSettled(s)) continue;

            PauseEntry? entry = s.IsTask && r.TaskLoopActive ? BuildTaskEntry(s) : BuildTurnEntry(s);
            if (entry == null) continue;

            r.PauseDumped = true;
            _pauseEntries[s.Id] = entry;
            fileChanged = true;

            ConversationPersistence.AppendJournal(s.Id, "PAUSE SAVED",
                $"kind={entry.Kind} nextRound={entry.NextRound} taskIndex={entry.TaskResumeIndex} dispatch={entry.HasDispatch}");
            AppendLog($"[Pause] '{s.Title}' is paused and saved.");

            RefreshSidebarBadge(s);
            if (ReferenceEquals(ResolvePauseOwner(_current ?? s), s))
                RefreshChromeForActiveSession();
        }

        if (fileChanged)
            PauseStateStore.Save(_pauseEntries.Values);

        // The global pause ends by itself when nothing is left that it could hold.
        if (_globalPauseRequested && _pauseEntries.Count == 0 && !GetPauseOwners().Any(o => o.IsPaused))
            _globalPauseRequested = false;
    }

    /// <summary>The queue of a paused chat can still be edited. Keep the saved copy in step with it.</summary>
    private bool RefreshDumpedInjections(ChatSession s)
    {
        if (!_pauseEntries.TryGetValue(s.Id, out var entry)) return false;

        PauseEntry? turnEntry   = entry.Kind == "turn" ? entry : entry.Child;
        ChatSession? turnSession = entry.Kind == "turn" ? s : (entry.Child == null ? null : _sessions.FirstOrDefault(x => x.Id == entry.Child.SessionId));
        if (turnEntry == null || turnSession == null) return false;

        int version = turnSession.Runtime.InjectionVersion;
        if (turnEntry.InjectionVersion == version) return false;

        turnEntry.Pending          = turnSession.Runtime.SnapshotPending();
        turnEntry.InjectionVersion = version;
        return true;
    }

    // ── Build the saved entry (UI thread, loops are frozen) ─────────────────

    /// <summary>Saves a chat turn that is frozen and describes where to continue. Null when it is not frozen after all.</summary>
    private PauseEntry? BuildTurnEntry(ChatSession s)
    {
        var r = s.Runtime;

        bool atFence = r.AtMainFence;
        DispatchResumeState? dispatch = null;

        if (!atFence)
        {
            // Paused in the middle of a round: the main loop waits for its Sub Agents and every one of them holds.
            if (!r.DispatchTracker.IsFrozen) return null;
            dispatch = r.DispatchTracker.Snapshot();
            if (dispatch == null) return null;
        }

        // What the AI loaded itself with load_tools during this turn belongs to the saved chat (SaveSession writes it).
        KeepAiLoadedTools(r, r.PausableTurnTools, r.PausableBaseToolNames);

        // The "⏸ Currently paused" widget goes into the turn's bubble BEFORE the chat is saved, so it is saved with it.
        BeginPauseMarker(s);

        var entry = new PauseEntry
        {
            Kind             = "turn",
            SessionId        = s.Id,
            NextRound        = r.NextRound,
            AgentName        = r.CurrentTurnAgentName,
            BackendName      = r.CurrentTurnBackendName,
            Pending          = r.SnapshotPending(),
            InjectionVersion = r.InjectionVersion,
        };

        if (dispatch != null)
        {
            // Every running Sub Agent has its own twin chat: save those too, then the Sub Agent state itself.
            foreach (string twinId in r.DispatchTracker.RunningTwinIds())
            {
                var twin = _sessions.FirstOrDefault(x => x.Id == twinId);
                if (twin == null) continue;
                BeginPauseMarker(twin);   // the Sub Agent's own chat shows the same widget
                ConversationPersistence.SaveSession(twin);
            }
            PauseStateStore.SaveDispatch(s.Id, dispatch);
            entry.HasDispatch = true;
        }
        else
        {
            PauseStateStore.DeleteDispatch(s.Id);
        }

        ConversationPersistence.SaveSession(s);
        return entry;
    }

    private PauseEntry? BuildTaskEntry(ChatSession task)
    {
        var r = task.Runtime;

        var entry = new PauseEntry
        {
            Kind         = "task",
            SessionId    = task.Id,
            ForwardQueue = new List<string>(r.TaskForwardMessageQueue),
        };

        if (r.TaskPausedAtIndex >= 0)
        {
            entry.TaskResumeIndex = r.TaskPausedAtIndex;   // between two steps
        }
        else
        {
            var child = CurrentChildOf(task);
            if (child == null) return null;
            PauseEntry? childEntry = BuildTurnEntry(child);
            if (childEntry == null) return null;

            child.Runtime.PauseDumped = true;
            entry.Child           = childEntry;
            entry.TaskResumeIndex = r.TaskCurrentUnitIndex;
        }

        ConversationPersistence.SaveSession(task);
        return entry;
    }

    // ── Clearing ──────────────────────────────────────────────────────────────

    /// <summary>Forgets the saved entry of a session (and its Sub Agent file). Rewrites / deletes the pause file.</summary>
    private void RemovePauseEntry(string sessionId, bool deleteDispatchFiles = true)
    {
        if (!_pauseEntries.Remove(sessionId, out var entry)) return;

        if (deleteDispatchFiles)
        {
            PauseStateStore.DeleteDispatch(sessionId);
            if (entry.Child != null) PauseStateStore.DeleteDispatch(entry.Child.SessionId);
        }
        PauseStateStore.Save(_pauseEntries.Values);
    }

    /// <summary>Ends every kind of pause of a session (it finished, was stopped, or its pause request became pointless).</summary>
    internal void ClearPauseState(ChatSession session)
    {
        var r = session.Runtime;
        bool had = session.IsPaused || r.PauseDumped || r.RestoredPause != null;

        // A pause widget that is still "currently paused" at this point means the paused turn was stopped.
        if (had) ClosePauseMarker(session, "stopped");

        session.IsPaused = false;
        r.SetUserPaused(false);
        r.PauseDumped    = false;
        r.RestoredPause  = null;
        r.TaskPausedAtIndex = -1;

        RemovePauseEntry(session.Id);

        if (!had) return;

        RefreshSidebarBadge(session);
        if (ReferenceEquals(session, _current))
            RefreshChromeForActiveSession();
    }

    // ── After an app start: load the pause file ─────────────────────────────

    /// <summary>
    /// Called once after the sessions are loaded. When sessions/pause_state.json exists, the paused chats / Tasks in it
    /// are shown as paused (no loop runs yet) and the global button offers "▶ Resume". A paused flag that is NOT backed
    /// by the file is dropped: it must never block a chat forever.
    /// </summary>
    private void RestorePauseState()
    {
        try
        {
            var entries = PauseStateStore.Load();
            var keep    = new HashSet<string>();

            foreach (var e in entries)
            {
                var s = _sessions.FirstOrDefault(x => x.Id == e.SessionId);
                if (s == null)
                {
                    AppendLog($"[Pause] Pause file entry for unknown session {e.SessionId} ignored.");
                    continue;
                }
                if (e.Kind == "task" && (!s.IsTask || e.TaskResumeIndex < 0 || e.TaskResumeIndex >= s.TaskUnits.Count))
                {
                    AppendLog($"[Pause] Pause file entry for Task '{s.Title}' does not fit the Task any more — ignored.");
                    continue;
                }

                _pauseEntries[s.Id] = e;
                keep.Add(s.Id);

                s.IsPaused = true;
                s.Runtime.SetUserPaused(true);
                s.Runtime.RestoredPause = e;
                s.Runtime.PauseDumped   = true;

                ChatSession? turnSession = s;
                PauseEntry?  turnEntry   = e;

                if (e.Kind == "task")
                {
                    turnEntry   = e.Child;
                    turnSession = e.Child == null ? null : _sessions.FirstOrDefault(x => x.Id == e.Child.SessionId);
                    if (turnSession != null)
                    {
                        turnSession.IsPaused = true;
                        turnSession.Runtime.SetUserPaused(true);
                        keep.Add(turnSession.Id);
                    }
                }

                // The injection queue lives in memory only: put the saved messages back.
                if (turnSession != null && turnEntry != null)
                {
                    foreach (var p in turnEntry.Pending)
                        turnSession.Runtime.EnqueueInjection(p);
                    turnEntry.InjectionVersion = turnSession.Runtime.InjectionVersion;
                }
            }

            foreach (var s in _sessions)
            {
                if (s.IsPaused && !keep.Contains(s.Id))
                {
                    s.IsPaused = false;
                    s.Runtime.SetUserPaused(false);
                }
            }

            // Some entries could not be used: write the file again without them (or delete it).
            if (_pauseEntries.Count != entries.Count)
                PauseStateStore.Save(_pauseEntries.Values);

            foreach (var s in _sessions)
                RefreshSidebarBadge(s);

            if (_pauseEntries.Count > 0)
                AppendLog($"[Pause] Pause file found: {_pauseEntries.Count} paused item(s). Click ▶ Resume to continue them.");

            if (_current != null)
                RefreshChromeForActiveSession();
        }
        catch (Exception ex)
        {
            SessionFileLogger.LogException("RestorePauseState failed", ex);
        }
    }

    // ── Start again from the pause file ─────────────────────────────────────

    private void StartFromRestored(ChatSession owner, PauseEntry entry)
    {
        // Nothing runs yet, so only flags have to be cleared before the work is started.
        owner.IsPaused = false;
        owner.Runtime.SetUserPaused(false);
        owner.Runtime.PauseDumped   = false;
        owner.Runtime.RestoredPause = null;

        if (entry.Child != null)
        {
            var child = _sessions.FirstOrDefault(x => x.Id == entry.Child.SessionId);
            if (child != null)
            {
                child.IsPaused = false;
                child.Runtime.SetUserPaused(false);
                child.Runtime.PauseDumped = false;
            }
        }

        RefreshSidebarBadge(owner);
        if (ReferenceEquals(owner, _current)) RefreshChromeForActiveSession();

        if (entry.Kind == "task")
            _ = RunTaskAsync(owner, entry.TaskResumeIndex, entry);
        else
            _ = ResumeTurnFromEntryAsync(owner, entry);
    }

    /// <summary>Continues a chat turn from its saved entry: the history is already in the chat; no new prompt is sent.</summary>
    private async Task ResumeTurnFromEntryAsync(ChatSession session, PauseEntry entry)
    {
        try
        {
            EnsureSessionPanelBuilt(session);   // loads session.json (history + messages) when it is not loaded yet

            DispatchResumeState? dispatch = entry.HasDispatch ? PauseStateStore.LoadDispatch(session.Id) : null;
            PauseStateStore.DeleteDispatch(session.Id);   // read once, now it lives in memory
            if (entry.HasDispatch && dispatch == null)
                AddStatusLine(session, "⚠  The Sub Agent state of this pause could not be read (pause_dispatch.json). " +
                                       "The Sub Agents that were running are reported to the AI as STOPPED; the AI continues from there.");

            session.TurnInProgress = true;

            // Continue in the SAME bubble: the pause widget (rebuilt from the saved chat) turns green and the new rounds
            // are written below it. Only when there is no such bubble, the normal behaviour (a new bubble) applies.
            bool sameBubble = AttachToPausedTurnBubble(session);
            ClosePauseMarker(session, "resumed");
            if (!sameBubble)
                AddStatusLine(session, $"▶  Resumed from the pause file — continuing at round {entry.NextRound}.");

            await RunLlmAsync(
                session, string.Empty, null,
                agentOverride:   string.IsNullOrEmpty(entry.AgentName) ? null : AgentRegistry.TryGet(entry.AgentName!),
                extraPreloadTools: null,
                backendOverride: entry.BackendName,
                resume:          new TurnResume { NextRound = entry.NextRound, Dispatch = dispatch });
        }
        catch (Exception ex)
        {
            SessionFileLogger.LogException($"Resuming '{session.Title}' from the pause file failed", ex);
            AddStatusLine(session, $"✗  Could not resume from the pause file: {ex.Message}");
        }
    }

    /// <summary>"Stop" on a session that is only paused in the file (nothing runs): throws the saved pause away.</summary>
    private void DiscardRestoredPause(ChatSession owner)
    {
        var entry = owner.Runtime.RestoredPause;
        var child = entry?.Child == null ? null : _sessions.FirstOrDefault(x => x.Id == entry.Child.SessionId);

        // The chat of the paused turn is not loaded yet after an app start: load it, so its pause widget can say "discarded".
        var turnSession = owner.IsTask ? child : owner;
        if (turnSession != null)
        {
            EnsureSessionPanelBuilt(turnSession);
            ClosePauseMarker(turnSession, "stopped");
            ConversationPersistence.SaveSession(turnSession);
        }

        ClearPauseState(owner);
        if (child != null) ClearPauseState(child);

        // Until it is sent again, the chat shows "interrupted" (its saved state says a turn was running).
        AddStatusLine(owner, "⏹  Paused work discarded. It was not continued.");
        ConversationPersistence.AppendJournal(owner.Id, "PAUSE DISCARDED", "User pressed Stop on a paused item.");
    }
}
