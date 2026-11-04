using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Avalonia.Controls;
using GTA.Core;

namespace GTA.App;

public sealed class ChatSessionRuntime : IPromptInjectionQueue
{
    /// <summary>True while an LLM turn (any/all agentic rounds) is in flight for this session.</summary>
    public bool IsProcessing { get; set; } = false;

    /// <summary>True when a turn finished for this session while it was NOT the active/visible session.</summary>
    public bool HasUnseenCompletion { get; set; } = false;

    /// <summary>Cancels the WHOLE turn (all agentic rounds) for this session.</summary>
    public CancellationTokenSource? Cts { get; set; } = null;

    /// <summary>Cancels ONLY the in-flight HTTP attempt of the current round (Retry button semantics).</summary>
    public Action? CurrentCancelAttempt { get; set; } = null;

    public TextBlock? ScheduledCountdownLabel { get; set; } = null;

    public TextBlock? TaskScheduledCountdownLabel { get; set; } = null;

    public int TaskCurrentUnitIndex { get; set; } = -1;

    public string? TaskCurrentChildSessionId { get; set; } = null;

    public bool TaskCancelRequested { get; set; } = false;

    /// <summary>Cancels an in-progress "Wait" unit's delay immediately when the task is cancelled mid-wait.</summary>
    public CancellationTokenSource? TaskWaitCts { get; set; } = null;

    public CancellationTokenSource? TaskChainWaitCts { get; set; } = null;

    public bool TaskHadError { get; set; } = false;

    public bool TaskWasCancelled { get; set; } = false;

    public bool LastTurnHadError { get; set; } = false;

    public TextBlock? TaskStatusLabel { get; set; } = null;

    public List<TextBlock>? TaskStepBadges { get; set; } = null;

    public List<Border>? TaskStepRows { get; set; } = null;

    public StackPanel? TaskStepsTreeRowsPanel { get; set; } = null;

    public TextBlock? TaskStepsTreeCountLabel { get; set; } = null;

    public List<TextBlock?>? SidebarStepBadges { get; set; } = null;

    /// <summary>The row Border for each sidebar step, parallel to SidebarStepBadges.</summary>
    public List<Border?>? SidebarStepRows { get; set; } = null;

    public int TaskSubTaskCounter { get; set; } = 0;

    public int? TaskLastInsertedIndex { get; set; } = null;

    public bool TaskRestartRequested { get; set; } = false;

    /// <summary>The 0-based TaskUnits index restart_task asked to rewind back to, or -1 once consumed/not requested.</summary>
    public int TaskRestartTargetIndex { get; set; } = -1;

    public string? TaskRestartInjectionMessage { get; set; } = null;

    public HashSet<int> TaskRestartingIndices { get; } = new();

    public int? TaskRestartPendingIndexShift { get; set; } = null;

    public List<string> TaskForwardMessageQueue { get; } = new();

    public AskQuestionsWindow? OpenAskQuestionsWindow { get; set; } = null;

    /// <summary>This session's own message-bubble StackPanel.</summary>
    public StackPanel? MessagesPanel { get; set; } = null;

    /// <summary>True once MessagesPanel has been built/populated at least once.</summary>
    public bool PanelBuilt { get; set; } = false;

    public List<LazyAttachedImageHost> AttachedImageHosts { get; } = new();

    public double LastScrollOffsetY { get; set; } = -1;

    public StackPanel?  CurrentTurnContentPanel { get; set; } = null;
    public ChatMessage? CurrentTurnMessage      { get; set; } = null;
    public int          CurrentTurnSequence     { get; set; } = 0;
    public string?      CurrentTurnModelName    { get; set; } = null;
    public TokenUsage    CurrentTurnUsageTotal  { get; set; } = default;

    public HashSet<string> PreloadedToolNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    // ── Injection queue ───────────────────────────────────────────────────
    // A plain list (not a Queue) so the user can edit / remove one message while it waits.

    private readonly object _injectionLock = new();
    private readonly List<PendingInjection> _pendingInjections = new();

    /// <summary>Raised (on ANY thread) whenever the queue changes: add, edit, remove, clear, or drained by the agent.</summary>
    public event Action? InjectionQueueChanged;

    private void RaiseInjectionQueueChanged() => InjectionQueueChanged?.Invoke();

    public void EnqueueInjection(PendingInjection injection)
    {
        lock (_injectionLock)
            _pendingInjections.Add(injection);
        RaiseInjectionQueueChanged();
    }

    public int PendingInjectionCount
    {
        get { lock (_injectionLock) return _pendingInjections.Count; }
    }

    /// <summary>Copies of everything still waiting, in delivery order. Safe to show / edit in the UI.</summary>
    public List<PendingInjection> SnapshotPending()
    {
        lock (_injectionLock)
        {
            var copy = new List<PendingInjection>(_pendingInjections.Count);
            foreach (var p in _pendingInjections) copy.Add(p.Clone());
            return copy;
        }
    }

    /// <summary>
    /// Replaces text / files / tools of one queued message. Keeps its place in the queue.
    /// Returns false if it is not queued any more (the agent already took it).
    /// </summary>
    public bool UpdateInjection(string id, string text, IEnumerable<string> files, IEnumerable<string> toolNames)
    {
        lock (_injectionLock)
        {
            var item = _pendingInjections.Find(p => p.Id == id);
            if (item == null) return false;
            item.Text            = text;
            item.Files           = new List<string>(files);
            item.ToolNamesToLoad = new List<string>(toolNames);
        }
        RaiseInjectionQueueChanged();
        return true;
    }

    /// <summary>Removes one queued message. Returns false if it is not queued any more.</summary>
    public bool RemoveInjection(string id)
    {
        bool removed;
        lock (_injectionLock)
            removed = _pendingInjections.RemoveAll(p => p.Id == id) > 0;
        if (removed) RaiseInjectionQueueChanged();
        return removed;
    }

    /// <summary>Moves one queued message up (-1) or down (+1). Returns false if it can not move.</summary>
    public bool MoveInjection(string id, int delta)
    {
        lock (_injectionLock)
        {
            int idx = _pendingInjections.FindIndex(p => p.Id == id);
            int to  = idx + delta;
            if (idx < 0 || to < 0 || to >= _pendingInjections.Count) return false;
            (_pendingInjections[idx], _pendingInjections[to]) = (_pendingInjections[to], _pendingInjections[idx]);
        }
        RaiseInjectionQueueChanged();
        return true;
    }

    /// <summary>Removes every queued message. Returns how many were removed.</summary>
    public int ClearInjections()
    {
        int n;
        lock (_injectionLock)
        {
            n = _pendingInjections.Count;
            _pendingInjections.Clear();
        }
        if (n > 0) RaiseInjectionQueueChanged();
        return n;
    }

    /// <summary>DrainPending — atomically removes and returns every injection queued so far (FIFO), or an empty list if none are pending.</summary>
    public List<PendingInjection> DrainPending()
    {
        List<PendingInjection> drained;
        lock (_injectionLock)
        {
            if (_pendingInjections.Count == 0) return new List<PendingInjection>();
            drained = new List<PendingInjection>(_pendingInjections);
            _pendingInjections.Clear();
        }
        RaiseInjectionQueueChanged();
        return drained;
    }

    // ── Pause ─────────────────────────────────────────────────────────────
    // BUG FIX: there used to be ONE shared "_isPaused" bool, set by the inject popup AND by the
    // sub-agent dispatch. When the sub agent finished it called Resume() and wiped the inject
    // popup's pause, so the agent "whooshed" into the next round. Now every owner has its own
    // hold. Releasing one never releases another.

    private readonly object _holdLock = new();
    private readonly HashSet<object> _holds = new();

    private volatile bool _isUserPaused = false;

    /// <summary>True when ANY owner (inject window, ...) or the user's own Pause holds this session.</summary>
    public bool IsPaused
    {
        get
        {
            if (_isUserPaused) return true;
            lock (_holdLock) return _holds.Count > 0;
        }
    }

    /// <summary>Hold (on=true) or release (on=false) the NEXT round on behalf of owner. Only touches owner's own hold.</summary>
    public void SetHold(object owner, bool on)
    {
        lock (_holdLock)
        {
            if (on) _holds.Add(owner);
            else    _holds.Remove(owner);
        }
    }

    public bool HasHold(object owner)
    {
        lock (_holdLock) return _holds.Contains(owner);
    }

    public void SetUserPaused(bool paused) => _isUserPaused = paused;

    private int _activeSubAgents = 0;

    /// <summary>How many dispatched sub agents are running for this session right now. Info only — it does NOT pause anything.</summary>
    public int ActiveSubAgentCount => Volatile.Read(ref _activeSubAgents);

    public void BeginSubAgentRun() => Interlocked.Increment(ref _activeSubAgents);

    public void EndSubAgentRun()
    {
        // Never go below zero.
        int cur;
        do { cur = Volatile.Read(ref _activeSubAgents); if (cur <= 0) return; }
        while (Interlocked.CompareExchange(ref _activeSubAgents, cur - 1, cur) != cur);
    }

    private volatile bool _isPauseActive = false;

    public bool IsPauseActive => _isPauseActive;

    /// <summary>OnPauseWaitStart.</summary>
    public void OnPauseWaitStart() => _isPauseActive = true;

    /// <summary>OnPauseWaitEnd.</summary>
    public void OnPauseWaitEnd() => _isPauseActive = false;

    public Action? TaskPauseButtonRefresh { get; set; } = null;

    public Dictionary<string, Action<string>> PendingToolResults { get; } = new();

    /// <summary>Queue for recording DisplayToolCall objects for disk persistence (FIFO).</summary>
    public Queue<DisplayToolCall> PendingToolRecords { get; } = new();

    /// <summary>Routing target: AddStatusLine sends sub-agent log lines into the active tool widget.</summary>
    public StackPanel? CurrentToolResultPanel { get; set; } = null;

    /// <summary>Parameters sub-panel of the most-recently created tool widget.</summary>
    public StackPanel? CurrentToolParamsPanel { get; set; } = null;

    /// <summary>Raw info-log lines of the current round (hidden behind a click on the round number).</summary>
    public StackPanel?  CurrentRoundContentPanel { get; set; } = null;
    public Stopwatch?   CurrentRoundStopwatch    { get; set; } = null;

    /// <summary>Panel inside the current round's row that receives its thinking / tool calls / text.</summary>
    public StackPanel?  CurrentRoundItemsPanel   { get; set; } = null;

    /// <summary>The "(N)" label of the current round — its hover popup is refreshed when the round's token usage arrives.</summary>
    public Control?     CurrentRoundBadge        { get; set; } = null;

    /// <summary>Front-end record of the current round (saved with the chat message).</summary>
    public DisplayRound? CurrentRoundRecord      { get; set; } = null;

    /// <summary>Reset per-turn scratch state at the start of a fresh assistant turn.</summary>
    public void ResetTurnScratchState()
    {
        CurrentTurnSequence     = 0;
        CurrentTurnUsageTotal   = default;
        CurrentTurnContentPanel = null;
        CurrentTurnMessage      = null;

        CurrentRoundContentPanel = null;
        CurrentRoundItemsPanel   = null;
        CurrentRoundBadge        = null;
        CurrentRoundRecord       = null;
        CurrentRoundStopwatch    = null;
    }

    /// <summary>Clear ALL turn state when a turn finishes or is cancelled (mirrors what the old window-global SetInputEnabled(true) used to do).</summary>
    public void ClearAfterTurnFinished()
    {
        CurrentTurnContentPanel = null;
        CurrentTurnMessage      = null;
        CurrentToolResultPanel  = null;
        CurrentToolParamsPanel  = null;
        Cts?.Dispose();
        Cts                     = null;
        PendingToolResults.Clear();
        PendingToolRecords.Clear();
        CurrentTurnSequence     = 0;

        CurrentRoundContentPanel = null;
        CurrentRoundItemsPanel   = null;
        CurrentRoundBadge        = null;
        CurrentRoundRecord       = null;
        CurrentRoundStopwatch    = null;
        CurrentCancelAttempt     = null;
        Interlocked.Exchange(ref _activeSubAgents, 0); // a cancelled turn never calls EndSubAgentRun

        IsProcessing = false;
    }
}
