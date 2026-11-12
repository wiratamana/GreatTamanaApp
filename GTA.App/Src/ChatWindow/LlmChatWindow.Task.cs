using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GTA.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace GTA.App;

public partial class LlmChatWindow
{
    /// <summary>Handler for the sidebar "📋 Task" button</summary>
    private void OpenTaskBuilderWindow()
    {
        var win = new TaskBuilderWindow(this);
        win.Show(this);
    }

    /// <summary>Reopens the builder window pre-filled with session's existing</summary>
    private void OpenTaskBuilderWindowForEdit(ChatSession session)
    {
        var win = new TaskBuilderWindow(this, session);
        win.Show(this);
    }

    /// <summary>Creates a brand-new ChatSession that is a Task container</summary>
    internal ChatSession CreateAndRunTask(List<TaskUnit> units, string? forcedBackend = null, string? titleOverride = null)
    {
        var session = new ChatSession
        {
            Title         = titleOverride ?? BuildTaskTitle(units),
            IsFullyLoaded = true,
            IsTask        = true,
            TaskUnits     = units,
            TaskBackendName = forcedBackend,
        };

        _sessions.Insert(0, session);
        var item = AddSessionListItem(session, prependToTop: true);

        EnsureTaskChildPlaceholderRows(session);

        SwitchSession(session, item);

        ConversationPersistence.SaveSession(session);

        _ = RunTaskAsync(session);

        return session;
    }

    /// <summary>Updates an EXISTING (not-yet-run) Task session's blueprint in place</summary>
    internal void RunExistingTask(ChatSession session, List<TaskUnit> units)
    {
        session.TaskUnits = units;

        session.TaskScheduledAt = null;
        session.Runtime.TaskScheduledCountdownLabel = null;

        RebuildTaskChildPlaceholderRows(session);

        ConversationPersistence.SaveSession(session);

        _ = RunTaskAsync(session);
    }

    /// <summary>Continues a Task session that was interrupted</summary>
    internal void ResumeInterruptedTask(ChatSession session)
    {
        // Paused in the pause file: continue EXACTLY where it paused (also the step that was paused in the middle of its turn).
        if (session.Runtime.RestoredPause != null)
        {
            ResumeOwner(session);
            return;
        }

        int historicalThrough = ComputeHistoricalCompletedThroughIndex(session.TaskUnits);
        int startIndex        = historicalThrough + 1;
        if (startIndex >= session.TaskUnits.Count) return; // already fully done

        if (session.IsPaused)
        {
            session.IsPaused = false;
            session.Runtime.SetUserPaused(false);
        }

        ConversationPersistence.SaveSession(session);

        _ = RunTaskAsync(session, startIndex);
    }

    /// <summary>Creates a brand-new Task-container ChatSession exactly like CreateAndRunTask</summary>
    internal void CreateAndScheduleTask(List<TaskUnit> units, DateTimeOffset when)
    {
        var session = new ChatSession
        {
            Title           = BuildTaskTitle(units),
            IsFullyLoaded   = true,
            IsTask          = true,
            TaskUnits       = units,
            TaskScheduledAt = when,
        };

        _sessions.Insert(0, session);
        var item = AddSessionListItem(session, prependToTop: true);

        EnsureTaskChildPlaceholderRows(session);

        SwitchSession(session, item);

        ConversationPersistence.SaveSession(session);
    }

    /// <summary>Updates an EXISTING (not-yet-run) Task session's blueprint in place and schedules it to start automatically at when</summary>
    internal void ScheduleExistingTask(ChatSession session, List<TaskUnit> units, DateTimeOffset when)
    {
        session.TaskUnits       = units;
        session.TaskScheduledAt = when;

        RebuildTaskChildPlaceholderRows(session);

        ConversationPersistence.SaveSession(session);

        RefreshSessionPlaceholderPanel(session);
        RefreshSidebarBadge(session);
    }

    private static string BuildTaskTitle(List<TaskUnit> units)
    {
        var firstPrompt = units.FirstOrDefault(
            u => u.Kind == TaskUnitKind.Prompt && !string.IsNullOrWhiteSpace(u.PromptText));
        // One line only: collapse line breaks and runs of spaces (prompts are often multi-line).
        string basis   = string.Join(" ",
            (firstPrompt?.PromptText ?? "Task").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        string trimmed = basis.Length > 40 ? basis[..40] + "…" : basis;
        return $"📋 {trimmed}";
    }

    /// <summary>Deletes a Task session that has NOT been run yet</summary>
    private void DeleteTaskBlueprint(ChatSession session)
    {
        if (_sidebarRows.TryGetValue(session.Id, out var refs))
        {
            _sessionListPanel.Children.Remove(refs.Item);
            _sidebarRows.Remove(session.Id);
        }

        if (_taskChildWrappers.TryGetValue(session.Id, out var wrapper))
        {
            _sessionListPanel.Children.Remove(wrapper);
            _taskChildWrappers.Remove(session.Id);
        }
        if (_taskChildContainers.TryGetValue(session.Id, out var _))
        {
            foreach (var key in _sidebarRows.Keys
                         .Where(k => k.StartsWith(session.Id + "::taskunit", StringComparison.Ordinal))
                         .ToList())
            {
                _sidebarRows.Remove(key);
            }
            _taskChildContainers.Remove(session.Id);
        }
        _taskFoldExpanded.Remove(session.Id);
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

    /// <summary>Fires any Task session whose ChatSession.TaskScheduledAt has been reached</summary>
    private void CheckScheduledTasks()
    {
        if (_sessions.Count == 0) return;

        var now = DateTimeOffset.Now;

        // Snapshot with ToArray() — see CheckScheduledPrompts for the same
        // "don't mutate the list you're iterating" rationale.
        foreach (var session in _sessions.ToArray())
        {
            if (session.IsTask &&
                session.TaskScheduledAt.HasValue &&
                session.TaskScheduledAt.Value <= now &&
                !session.IsPaused &&
                !_globalPauseRequested) // "⏸ Pause": nothing new starts while everything is paused
            {
                FireScheduledTask(session);
            }
        }
    }

    /// <summary>Fires session's scheduled Task blueprint right now</summary>
    private void FireScheduledTask(ChatSession session)
    {
        if (session.TaskHasRun) return; // already started — shouldn't normally happen

        session.TaskScheduledAt = null; // pending → fired, immediately (no race)
        session.Runtime.TaskScheduledCountdownLabel = null;
        RefreshSidebarBadge(session);

        _ = RunTaskAsync(session);
    }

    /// <summary>Refreshes the ACTIVE session's live "⏰ Scheduled Task" countdown label</summary>
    private void UpdateTaskScheduledCountdownLabel()
    {
        var session = _current;
        if (session == null) return;
        if (!session.TaskScheduledAt.HasValue) return;

        var label = session.Runtime.TaskScheduledCountdownLabel;
        if (label == null) return;

        SetCountdownText(label, session.TaskScheduledAt.Value);
    }

    /// <summary>Picks the correct main-area placeholder for an empty</summary>
    private Control BuildSessionPlaceholder(ChatSession session)
    {
        if (session.IsTask)
        {
            if (session.TaskHasRun)
                return BuildTaskFinishedOverviewPlaceholder(session);

            return session.TaskScheduledAt.HasValue
                ? BuildTaskScheduledPlaceholder(session)
                : BuildTaskBlueprintPlaceholder(session);
        }

        return session.ScheduledAt.HasValue
            ? BuildScheduledPlaceholder(session) // see LlmChatWindow.Scheduler.cs
            : BuildWelcomePlaceholder();          // see LlmChatWindow.Session.cs
    }

    /// <summary>Rebuilds session's ALREADY-built persistent panel's placeholder content in place</summary>
    private void RefreshSessionPlaceholderPanel(ChatSession session)
    {
        if (!session.Runtime.PanelBuilt) return;
        if (session.Messages.Count != 0) return;

        var panel = session.Runtime.MessagesPanel;
        if (panel == null) return;

        panel.Children.Clear();
        panel.Children.Add(BuildSessionPlaceholder(session));

        if (ReferenceEquals(session, _current))
            _messagesScroll.ScrollToEnd();
    }

    /// <summary>
    /// Executes session's ChatSession.TaskUnits strictly in array order, starting at startIndex.
    /// resumeEntry (only after a restart from the pause file, see LlmChatWindow.PauseDump.cs): where the Task was paused —
    /// its Child entry is the step chat that was paused in the middle of its turn, and that turn is continued, not restarted.
    /// </summary>
    private async Task RunTaskAsync(ChatSession session, int startIndex = 0, PauseEntry? resumeEntry = null)
    {
        var runtime = session.Runtime;
        runtime.TaskCancelRequested = false;
        runtime.TaskHadError        = false;
        runtime.TaskWasCancelled    = false;
        // "insert next" sub-task bookkeeping (see InsertSubTaskUnitLive) —
        // fresh for every run, exactly like the fields just above.
        runtime.TaskSubTaskCounter    = 0;
        runtime.TaskLastInsertedIndex = null;
        runtime.TaskRestartRequested          = false;
        runtime.TaskRestartTargetIndex        = -1;
        runtime.TaskRestartInjectionMessage   = null;
        runtime.TaskRestartPendingIndexShift  = null;
        runtime.TaskRestartingIndices.Clear();
        runtime.TaskForwardMessageQueue.Clear();

        // Pause bookkeeping (see LlmChatWindow.PauseDump.cs)
        runtime.TaskLoopActive      = true;
        runtime.TaskPausedAtIndex   = -1;
        runtime.TaskStepPausedEarly = false;
        if (resumeEntry != null)
            foreach (string forwarded in resumeEntry.ForwardQueue)
                runtime.TaskForwardMessageQueue.Add(forwarded);

        // A global ⏸ Pause is on while this Task starts: it holds in front of its first step.
        if (_globalPauseRequested && !session.IsPaused)
        {
            session.IsPaused = true;
            runtime.SetUserPaused(true);
        }

        session.TaskHasRun      = true;
        session.TaskBackendName ??= _selectedBackend;
        ConversationPersistence.SaveSession(session);

        Border?    bannerBorder  = null;
        TextBlock? statusLabel   = null;
        Button?    cancelBtn     = null;
        Button?    addTaskBtn    = null;
        Button?    insertNextBtn = null;
        Button?    pauseBtn      = null;
        bool       wasCancelled  = false;
        bool       hadError      = false;

        try
        {
            EnsureSessionPanelBuilt(session);
            var panel = session.Runtime.MessagesPanel!;

            if (session.Messages.Count == 0)
                panel.Children.Clear();

            (bannerBorder, statusLabel, cancelBtn, addTaskBtn, insertNextBtn, pauseBtn) = BuildTaskRunningBanner(session);
            panel.Children.Add(bannerBorder);
            runtime.TaskStatusLabel = statusLabel;

            if (ReferenceEquals(session, _current))
            {
                _messagesScroll.ScrollToEnd();
                RefreshChromeForActiveSession();
            }
            RefreshSidebarBadge(session);

            // Continuing after a pause: the steps in front of the resume point are done.
            for (int k = 0; k < startIndex && k < session.TaskUnits.Count; k++)
                MarkTaskStepCompleted(session, k);

            for (int i = startIndex; i < session.TaskUnits.Count; i++)
            {
                if (runtime.TaskCancelRequested) break;

                // ⏸ Pause (or a 💉 hold): hold in front of the next step. This is a "fence" of the Task: the window
                // saves the Task here (TaskPausedAtIndex says in front of which step it holds).
                if ((runtime.IsPauseRequested || runtime.IsPaused) && !runtime.TaskCancelRequested)
                {
                    runtime.OnPauseWaitStart();
                    runtime.TaskPausedAtIndex = i;
                    try
                    {
                        UpdateTaskStatusLabel(session, "⏸  Paused — holding before the next step starts…");
                        while ((runtime.IsPauseRequested || runtime.IsPaused) && !runtime.TaskCancelRequested)
                            await Task.Delay(TimeSpan.FromMilliseconds(200));
                        if (!runtime.TaskCancelRequested)
                            UpdateTaskStatusLabel(session, "▶  Resumed — continuing…");
                    }
                    finally
                    {
                        runtime.TaskPausedAtIndex = -1;
                        runtime.OnPauseWaitEnd();
                    }
                }

                if (runtime.TaskCancelRequested) break;

                runtime.TaskCurrentUnitIndex = i;
                var unit  = session.TaskUnits[i];
                int total = session.TaskUnits.Count;

                MarkTaskStepRunning(session, i);

                bool stepFailed = false;

                if (unit.Kind == TaskUnitKind.Prompt)
                {
                    UpdateTaskStatusLabel(session, $"▶  Step {i + 1}/{total} — sending prompt…");

                    // After a restart, the step that was paused in the middle of its turn keeps its OWN chat (no new one).
                    ChatSession child;
                    PauseEntry? childResume = null;
                    var existingChild = (resumeEntry?.Child != null && i == startIndex)
                        ? _sessions.FirstOrDefault(s => s.Id == resumeEntry.Child.SessionId)
                        : null;
                    if (existingChild != null)
                    {
                        child       = existingChild;
                        childResume = resumeEntry!.Child;
                    }
                    else
                    {
                        child = CreateChildTaskSession(session, unit, i);
                    }
                    runtime.TaskCurrentChildSessionId = child.Id;

                    if (i == runtime.TaskRestartTargetIndex && runtime.TaskRestartInjectionMessage != null)
                    {
                        child.Runtime.EnqueueInjection(new PendingInjection
                        {
                            Text = runtime.TaskRestartInjectionMessage,
                        });
                        runtime.TaskRestartInjectionMessage = null;
                        runtime.TaskRestartTargetIndex      = -1;
                    }

                    if (runtime.TaskForwardMessageQueue.Count > 0)
                    {
                        foreach (string forwardedMessage in runtime.TaskForwardMessageQueue)
                        {
                            child.Runtime.EnqueueInjection(new PendingInjection
                            {
                                Text = forwardedMessage,
                            });
                        }
                        runtime.TaskForwardMessageQueue.Clear();
                    }

                    var stepAttachments = unit.Attachments.Count > 0
                        ? new List<string>(unit.Attachments)
                        : null;
                    var stepPreloadTools = unit.PreloadedToolNames.Count > 0
                        ? unit.PreloadedToolNames
                        : null;

                    if (childResume != null)
                    {
                        // This step's turn was paused in the middle (see LlmChatWindow.PauseDump.cs): continue it.
                        await ResumeTurnFromEntryAsync(child, childResume);
                    }
                    else
                    {
                        await SendTextToSession(
                            child, unit.PromptText, stepAttachments, null, stepPreloadTools,
                            autoTitle: false, backendOverride: session.TaskBackendName);
                    }

                    runtime.TaskCurrentChildSessionId = null;

                    stepFailed = child.Runtime.LastTurnHadError;
                }
                else if (unit.Kind == TaskUnitKind.Wait)
                {
                    await RunWaitUnitAsync(session, unit, i, total);
                }
                else // TaskUnitKind.ChainTask
                {
                    stepFailed = await RunChainTaskUnitAsync(session, unit, i, total);
                }
                // A Wait / Chain step that was cut short by ⏸ Pause is NOT finished: run it again after the resume.
                // (i-- then the for-loop's i++ lands on the same step; the hold at the top of the loop comes first.)
                if (runtime.TaskStepPausedEarly)
                {
                    runtime.TaskStepPausedEarly = false;
                    if (!runtime.TaskCancelRequested)
                    {
                        i--;
                        continue;
                    }
                }


                if (runtime.TaskRestartPendingIndexShift.HasValue && i >= runtime.TaskRestartPendingIndexShift.Value)
                {
                    i++;
                    runtime.TaskCurrentUnitIndex         = i;
                    runtime.TaskRestartPendingIndexShift = null;
                }

                if (runtime.TaskCancelRequested)
                {
                    MarkTaskStepCancelled(session, i);

                    for (int j = i + 1; j < session.TaskUnits.Count; j++)
                        MarkTaskStepCancelled(session, j);

                    break;
                }

                if (stepFailed)
                {
                    runtime.TaskHadError = true;
                    MarkTaskStepError(session, i);

                    for (int j = i + 1; j < session.TaskUnits.Count; j++)
                        MarkTaskStepError(session, j);

                    break;
                }

                if (runtime.TaskRestartRequested)
                {
                    runtime.TaskRestartingIndices.Add(i);
                    MarkTaskStepRestarting(session, i);
                    runtime.TaskRestartRequested = false;
                    i = runtime.TaskRestartTargetIndex - 1;
                    continue;
                }
                MarkTaskStepCompleted(session, i);
            }

            wasCancelled = runtime.TaskCancelRequested;
            hadError      = runtime.TaskHadError;
        }
        catch (OperationCanceledException)
        {
            wasCancelled = true;
        }
        catch (Exception ex)
        {
            SessionFileLogger.Log($"[RunTaskAsync] Unexpected error in task '{session.Title}': {ex}");
            hadError = true;
        }

        runtime.TaskCurrentUnitIndex = -1;
        runtime.TaskLoopActive       = false;
        runtime.TaskPausedAtIndex    = -1;
        runtime.TaskStepPausedEarly  = false;
        runtime.TaskCancelRequested  = false;
        runtime.TaskStatusLabel      = null;
        runtime.TaskPauseButtonRefresh = null; // see ChatSessionRuntime's own doc comment

        runtime.TaskWasCancelled = wasCancelled;
        if (hadError) runtime.TaskHadError = true;

        // The Task is over (done, cancelled or failed): its pause, and its entry in the pause file, end here.
        ClearPauseState(session);

        if (bannerBorder != null && statusLabel != null && cancelBtn != null &&
            addTaskBtn != null && insertNextBtn != null && pauseBtn != null)
        {
            FinalizeTaskBanner(bannerBorder, statusLabel, cancelBtn, addTaskBtn, insertNextBtn, pauseBtn, wasCancelled, hadError);
        }

        if (ReferenceEquals(session, _current))
            RefreshChromeForActiveSession();
        RefreshSidebarBadge(session);

        ConversationPersistence.SaveSession(session);
    }

    /// <summary>Creates a brand-new, completely ORDINARY chat session that runs ONE "Prompt" unit's own conversation</summary>
    private ChatSession CreateChildTaskSession(ChatSession parent, TaskUnit unit, int index)
    {
        string? previousChildSessionId = unit.ChildSessionId;

        var child = new ChatSession
        {
            Title         = unit.SubTaskNumber > 0
                ? $"Sub-Task {unit.SubTaskNumber}: {TruncateOneLine(unit.PromptText, 40)}"
                : unit.RestartLabelIndex >= 0
                    ? $"Task {unit.RestartLabelIndex + 1}: {TruncateOneLine(unit.PromptText, 40)}"
                    : $"Task {index + 1}: {TruncateOneLine(unit.PromptText, 40)}",
            IsFullyLoaded = true,
            ParentTaskId  = parent.Id,
        };

        unit.ChildSessionId = child.Id;

        _sessions.Add(child);
        UpgradeTaskPlaceholderToChildSession(parent, child, index, previousChildSessionId);

        ConversationPersistence.SaveSession(child);
        ConversationPersistence.SaveSession(parent);

        return child;
    }

    /// <summary>Switches into the CHILD session that ran a given step</summary>
    private void OpenChildSessionFromTree(string childSessionId)
    {
        var child = _sessions.FirstOrDefault(s => s.Id == childSessionId);
        if (child == null) return;

        if (_sidebarRows.TryGetValue(child.Id, out var refs))
            SwitchSession(child, refs.Item);
    }

    /// <summary>Pause for a "Wait" unit's configured duration</summary>
    private async Task RunWaitUnitAsync(ChatSession session, TaskUnit unit, int unitIndex, int total)
    {
        var runtime = session.Runtime;
        runtime.TaskWaitCts = new CancellationTokenSource();
        var ct = runtime.TaskWaitCts.Token;

        var deadline = unit.WaitUntil.HasValue
            ? new DateTimeOffset(DateTime.SpecifyKind(unit.WaitUntil.Value, DateTimeKind.Local))
            : DateTimeOffset.Now.AddSeconds(unit.WaitSeconds);

        try
        {
            while (!runtime.TaskCancelRequested)
            {
                var remaining = deadline - DateTimeOffset.Now;
                if (remaining <= TimeSpan.Zero) break;
                // ⏸ Pause: stop waiting now. The deadline is saved as an absolute time, so the step continues
                // with the REST of the wait after the resume (pause time counts as waited time, like any wall clock).
                if (runtime.IsPauseRequested)
                {
                    unit.WaitUntil = deadline.LocalDateTime;
                    runtime.TaskStepPausedEarly = true;
                    break;
                }


                UpdateTaskStatusLabel(session,
                    $"⏱  Step {unitIndex + 1}/{total} — waiting {FormatCountdown(remaining)}…");

                await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            runtime.TaskWaitCts?.Dispose();
            runtime.TaskWaitCts = null;
        }
    }

    /// <summary>Runs a "⛓ Chain Task" unit</summary>
    private async Task<bool> RunChainTaskUnitAsync(ChatSession session, TaskUnit unit, int unitIndex, int total)
    {
        var runtime = session.Runtime;
        string label = unit.ChainTaskSessionTitle ?? "(unknown task)";

        if (string.IsNullOrEmpty(unit.ChainTaskSessionId))
        {
            UpdateTaskStatusLabel(session, $"⛓  Step {unitIndex + 1}/{total} — no target task configured");
            return true; // misconfigured — treat as a failed step
        }

        runtime.TaskChainWaitCts = new CancellationTokenSource();
        var ct = runtime.TaskChainWaitCts.Token;

        try
        {
            while (true)
            {
                if (runtime.TaskCancelRequested) return false; // outer loop treats this as "Cancelled", not "Error"
                // ⏸ Pause: leave this step now. It is run again after the resume (it re-checks the other task).
                if (runtime.IsPauseRequested)
                {
                    runtime.TaskStepPausedEarly = true;
                    return false;
                }


                var target = _sessions.FirstOrDefault(s => s.Id == unit.ChainTaskSessionId);
                if (target == null)
                {
                    UpdateTaskStatusLabel(session,
                        $"⛓  Step {unitIndex + 1}/{total} — target task '{label}' no longer exists");
                    return true;
                }

                if (!IsTaskSessionStillRunning(target))
                {
                    // Stopped running — figure out WHY so this step reflects
                    // the same outcome instead of blindly assuming success.
                    if (!target.TaskHasRun)
                    {
                        UpdateTaskStatusLabel(session,
                            $"⛓  Step {unitIndex + 1}/{total} — target task '{label}' never ran");
                        return true;
                    }

                    if (target.Runtime.TaskWasCancelled || target.Runtime.TaskHadError)
                    {
                        UpdateTaskStatusLabel(session,
                            $"⛓  Step {unitIndex + 1}/{total} — target task '{label}' " +
                            (target.Runtime.TaskWasCancelled ? "was cancelled" : "failed"));
                        return true;
                    }

                    // Finished cleanly — this step's whole job is done.
                    return false;
                }

                UpdateTaskStatusLabel(session, $"⛓  Step {unitIndex + 1}/{total} — waiting for '{label}' to finish…");
                await Task.Delay(TimeSpan.FromMilliseconds(300), ct);
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            runtime.TaskChainWaitCts?.Dispose();
            runtime.TaskChainWaitCts = null;
        }
    }

    /// <summary>Adds a brand-new "Prompt" TaskUnit to the SAME running Task blueprint whose one step's own child session is what is...</summary>
    internal string DelegateNewTaskStep(string prompt, string position)
    {
        var callingSession = DelegateTaskContext.CurrentSession;
        if (callingSession == null)
        {
            return "Error: delegate_task could not determine which session is calling it " +
                   "(no active session context) — this should not normally happen.";
        }

        string? parentId = callingSession.ParentTaskId;
        if (string.IsNullOrEmpty(parentId))
        {
            return "Error: delegate_task can only be used from a step that is itself running " +
                   "as part of a Task blueprint (see the '📋 Task' feature) — this chat is not " +
                   "one of those, so there is no running Task to add a new step to.";
        }

        bool insertNext = string.Equals(position, "next", StringComparison.OrdinalIgnoreCase);

        using var doneSignal = new ManualResetEventSlim(false);
        string result = string.Empty;

        void DoWork()
        {
            var parentTask = _sessions.FirstOrDefault(s => s.Id == parentId && s.IsTask);
            if (parentTask == null)
            {
                result = "Error: the parent Task session could not be found (it may have been deleted).";
                doneSignal.Set();
                return;
            }

            int curIdx = parentTask.Runtime.TaskCurrentUnitIndex;
            if (curIdx < 0)
            {
                result = "Error: the parent Task is not currently running (it already finished, " +
                         "was cancelled, or never started) — delegate_task only works while it is " +
                         "actively executing.";
                doneSignal.Set();
                return;
            }

            List<string> inheritedTools = curIdx < parentTask.TaskUnits.Count
                ? new List<string>(parentTask.TaskUnits[curIdx].PreloadedToolNames)
                : new List<string>();

            var newUnit = new TaskUnit
            {
                Kind               = TaskUnitKind.Prompt,
                PromptText         = prompt,
                PreloadedToolNames = inheritedTools,
            };

            if (insertNext)
            {
                InsertSubTaskUnitLive(parentTask, newUnit);

                result = $"✅ Delegated — inserted as Sub-Task {newUnit.SubTaskNumber}, right after the " +
                         $"currently running step in Task '{parentTask.Title}' — it jumps ahead of any " +
                         "already-queued steps and will run automatically once the sequence reaches it.";
            }
            else
            {
                int newIndex = AppendTaskUnitLive(parentTask, newUnit);

                result = $"✅ Delegated — queued as Task {newIndex + 1} under the running Task " +
                         $"'{parentTask.Title}'. It will run automatically, in order, once the " +
                         "sequence reaches it.";
            }
            doneSignal.Set();
        }

        if (Dispatcher.UIThread.CheckAccess())
            DoWork();
        else
            Dispatcher.UIThread.Post(DoWork);

        doneSignal.Wait();
        return result;
    }

    /// <summary>Core "append one more step to the end of a running Task's own ChatSession.TaskUnits" logic</summary>
    private int AppendTaskUnitLive(ChatSession parentTask, TaskUnit newUnit)
    {
        int newIndex = parentTask.TaskUnits.Count;
        parentTask.TaskUnits.Add(newUnit);

        var badges = parentTask.Runtime.SidebarStepBadges;
        var rows   = parentTask.Runtime.SidebarStepRows;
        if (badges != null && rows != null)
        {
            var (item, _, badgeText, _) = AddTaskUnitPlaceholderRow(parentTask, newUnit, newIndex);
            badges.Add(badgeText);
            rows.Add(item);
        }

        AppendLiveTaskStepRow(parentTask, newUnit, newIndex);

        ConversationPersistence.SaveSession(parentTask);

        return newIndex;
    }

    /// <summary>Handler for the running-task banner's "➕ Add Task" button</summary>
    private async void AddManualTaskStep(ChatSession session)
    {
        var win = new TaskUnitEditWindow(TaskUnitKind.Prompt);
        await win.ShowDialog(this);

        if (win.Result == null) return;

        AppendTaskUnitLive(session, win.Result);
    }

    /// <summary>Core "splice a new step in right after the currently-running one" logic</summary>
    private int InsertSubTaskUnitLive(ChatSession parentTask, TaskUnit newUnit)
    {
        var rt     = parentTask.Runtime;
        int curIdx = rt.TaskCurrentUnitIndex;
        int baseline = curIdx >= 0 ? curIdx : parentTask.TaskUnits.Count - 1;

        int insertIndex = (rt.TaskLastInsertedIndex.HasValue && rt.TaskLastInsertedIndex.Value >= baseline)
            ? rt.TaskLastInsertedIndex.Value + 1
            : baseline + 1;
        insertIndex = Math.Clamp(insertIndex, 0, parentTask.TaskUnits.Count);

        newUnit.SubTaskNumber = ++rt.TaskSubTaskCounter;
        parentTask.TaskUnits.Insert(insertIndex, newUnit);
        rt.TaskLastInsertedIndex = insertIndex;

        InsertLiveTaskStepRow(parentTask, newUnit, insertIndex);       // content-area "Parent" tree
        InsertTaskUnitPlaceholderRowAt(parentTask, newUnit, insertIndex); // sidebar nested tree

        ConversationPersistence.SaveSession(parentTask);

        return insertIndex;
    }

    /// <summary>Splices a restart_task "replay" unit into parentTask's blueprint at an EXPLICIT</summary>
    private int InsertRestartReplacementUnit(ChatSession parentTask, TaskUnit newUnit, int insertIndex)
    {
        parentTask.TaskUnits.Insert(insertIndex, newUnit);

        InsertLiveTaskStepRow(parentTask, newUnit, insertIndex);          // content-area "Parent" tree
        InsertTaskUnitPlaceholderRowAt(parentTask, newUnit, insertIndex); // sidebar nested tree

        ConversationPersistence.SaveSession(parentTask);

        return insertIndex;
    }

    /// <summary>Handler for the running-task banner's "⤴ Insert Next" button</summary>
    private async void AddManualSubTaskStep(ChatSession session)
    {
        var win = new TaskUnitEditWindow(TaskUnitKind.Prompt);
        await win.ShowDialog(this);

        if (win.Result == null) return;

        InsertSubTaskUnitLive(session, win.Result);
    }

    /// <summary>Immediately cancels a running task for good</summary>
    private void CancelRunningTask(ChatSession session)
    {
        session.Runtime.TaskCancelRequested = true;

        var childId = session.Runtime.TaskCurrentChildSessionId;
        if (childId != null)
        {
            var child = _sessions.FirstOrDefault(s => s.Id == childId);
            try { child?.Runtime.Cts?.Cancel(); }
            catch { /* already cancelled/disposed — ignore */ }
        }

        try { session.Runtime.TaskWaitCts?.Cancel(); }
        catch { /* already cancelled/disposed — ignore */ }

        try { session.Runtime.TaskChainWaitCts?.Cancel(); }
        catch { /* already cancelled/disposed — ignore */ }
    }

    /// <summary>True while session is an actively-executing Task container</summary>
    internal bool IsTaskSessionStillRunning(ChatSession session) =>
        session.IsTask &&
        (session.Runtime.TaskCurrentUnitIndex >= 0 ||
         session.Runtime.TaskLoopActive ||                 // also while it holds in front of its FIRST step (paused)
         session.Runtime.RestoredPause != null);           // paused in the pause file: it will continue, so it is not "finished"

    /// <summary>Every session currently satisfying IsTaskSessionStillRunning right now</summary>
    internal List<ChatSession> GetCurrentlyRunningTaskSessions(string? excludeSessionId = null) =>
        _sessions.Where(s => IsTaskSessionStillRunning(s) && s.Id != excludeSessionId).ToList();

    private static void UpdateTaskStatusLabel(ChatSession session, string text)
    {
        var label = session.Runtime.TaskStatusLabel;
        if (label == null) return;

        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() =>
            {
                var stillLabel = session.Runtime.TaskStatusLabel;
                if (stillLabel != null) stillLabel.Text = text;
            });
            return;
        }
        label.Text = text;
    }

    private static readonly SolidColorBrush ColTaskRunningBadge = new(Color.FromRgb(230, 190, 80));

    private void MarkTaskStepRunning(ChatSession session, int index)
    {
        session.Runtime.TaskRestartingIndices.Remove(index);
        SetTaskStepVisual(session, index, "▶  Running…", ColTaskRunningBadge, highlightRow: true);
    }

    private void MarkTaskStepRestarting(ChatSession session, int index) =>
        SetTaskStepVisual(session, index, "⟲  Restarting…", ColRetryAmber, highlightRow: false, sidebarState: TaskRowBadgeState.Restarting);

    private void MarkTaskStepCompleted(ChatSession session, int index) =>
        SetTaskStepVisual(session, index, "✅  Completed", ColDoneBadgeFg, highlightRow: false);

    /// <summary>A step restart_task has permanently superseded</summary>
    private void MarkTaskStepOld(ChatSession session, int index) =>
        SetTaskStepVisual(session, index, "✅  Completed [OLD]", ColMuted, highlightRow: false, sidebarState: TaskRowBadgeState.Old);

    /// <summary>A step the user cancelled</summary>
    private void MarkTaskStepCancelled(ChatSession session, int index) =>
        SetTaskStepVisual(session, index, "⏹  Skipped", ColMuted, highlightRow: false, sidebarState: TaskRowBadgeState.Skipped);

    /// <summary>A step that actually FAILED</summary>
    private void MarkTaskStepError(ChatSession session, int index) =>
        SetTaskStepVisual(session, index, "✗  Error", ColRunningRed, highlightRow: false, sidebarState: TaskRowBadgeState.Error);

    /// <summary>Repaints ONE step's badge/row in the content-area "Parent" overview tree</summary>
    private void SetTaskStepVisual(
        ChatSession session, int index, string text, IBrush color, bool highlightRow,
        TaskRowBadgeState? sidebarState = null)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => SetTaskStepVisual(session, index, text, color, highlightRow, sidebarState));
            return;
        }

        var badges = session.Runtime.TaskStepBadges;
        var rows    = session.Runtime.TaskStepRows;
        if (badges != null && index >= 0 && index < badges.Count)
        {
            badges[index].Text       = text;
            badges[index].Foreground = color;

            if (rows != null && index < rows.Count)
                rows[index].Background = highlightRow ? BgActiveItem : Brushes.Transparent;
        }

        if (index >= 0 && index < session.TaskUnits.Count)
        {
            string? childId = session.TaskUnits[index].ChildSessionId;
            if (childId != null)
            {
                var childSession = _sessions.Find(s => s.Id == childId);
                if (childSession != null) RefreshSidebarBadge(childSession);
            }
        }

        if (sidebarState.HasValue)
        {
            string placeholderKey = TaskUnitPlaceholderKey(session.Id, index);
            if (_sidebarRows.TryGetValue(placeholderKey, out var sidebarRefs))
                SetSidebarTaskBadgeState(sidebarRefs.Badge, sidebarRefs.BadgeText, sidebarState.Value);
        }
    }

    /// <summary>Builds the shared foldable "Parent" overview widget used by ALL THREE of the pre-run blueprint placeholder</summary>
    private Expander BuildTaskStepsTree(
        ChatSession session, IReadOnlyList<TaskUnit> units, bool liveBadges,
        int historicalCompletedThroughIndex = -1)
    {
        int promptCount = units.Count(u => u.Kind == TaskUnitKind.Prompt);
        int waitCount    = units.Count(u => u.Kind == TaskUnitKind.Wait);
        int chainCount   = units.Count(u => u.Kind == TaskUnitKind.ChainTask);

        var headerPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        headerPanel.Children.Add(new TextBlock
        {
            Text              = "📋  Parent",
            FontSize          = 13,
            FontWeight        = FontWeight.SemiBold,
            Foreground        = ColText,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var countLabel = new TextBlock
        {
            Text              = $"— {units.Count} step(s)  ({promptCount} prompt, {waitCount} wait, {chainCount} chain)",
            FontSize          = 11,
            Foreground        = ColMuted,
            VerticalAlignment = VerticalAlignment.Center,
        };
        headerPanel.Children.Add(countLabel);

        var rowsPanel = new StackPanel { Spacing = 4, Margin = new Thickness(20, 6, 4, 6) };

        List<TextBlock>? badges = liveBadges ? new List<TextBlock>(units.Count) : null;
        List<Border>?    rows   = liveBadges ? new List<Border>(units.Count)    : null;

        for (int i = 0; i < units.Count; i++)
        {
            var (rowBorder, badge) = BuildTaskStepRowControl(
                units[i], i, liveBadges, historicalCompletedThroughIndex);

            rowsPanel.Children.Add(rowBorder);
            badges?.Add(badge);
            rows?.Add(rowBorder);
        }

        if (liveBadges)
        {
            session.Runtime.TaskStepBadges = badges;
            session.Runtime.TaskStepRows   = rows;

            session.Runtime.TaskStepsTreeRowsPanel  = rowsPanel;
            session.Runtime.TaskStepsTreeCountLabel = countLabel;
        }

        var scrollableRows = new ScrollViewer
        {
            Content                       = rowsPanel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            MaxHeight                     = 240, // bounded → scrolls instead of growing forever
        };

        var contentBorder = new Border
        {
            Background = BgCodeBlock,
            Child      = scrollableRows,
        };

        var expander = new Expander
        {
            Header     = headerPanel,
            Content    = contentBorder,
            IsExpanded = true,
            Margin     = new Thickness(0, 6, 0, 0),
        };
        UiTheme.StyleExpander(
            expander,
            headerBackground:  BgAiBubble,
            headerForeground:  ColText,
            contentBackground: BgCodeBlock);

        return expander;
    }

    /// <summary>Builds ONE row control for the "Parent" overview tree</summary>
    private (Border Row, TextBlock Badge) BuildTaskStepRowControl(
        TaskUnit unit, int index, bool liveBadges, int historicalCompletedThroughIndex)
    {
        string icon = unit.Kind switch
        {
            TaskUnitKind.Prompt    => "💬",
            TaskUnitKind.Wait      => "⏱",
            TaskUnitKind.ChainTask => "⛓",
            _                      => "❓",
        };
        string body = unit.Kind switch
        {
            TaskUnitKind.Prompt    => TruncateOneLine(unit.PromptText, 90),
            TaskUnitKind.Wait      => unit.WaitSummary,
            TaskUnitKind.ChainTask => $"Chain after: {unit.ChainTaskSessionTitle ?? "(unknown task)"}",
            _                      => string.Empty,
        };

        var titleBlock = new TextBlock
        {
            Text              = unit.SubTaskNumber > 0
                ? $"Sub-Task {unit.SubTaskNumber}"
                : unit.RestartLabelIndex >= 0
                    ? $"Task {unit.RestartLabelIndex + 1}"
                    : $"Task {index + 1}",
            FontSize          = 12,
            FontWeight        = FontWeight.SemiBold,
            Foreground        = ColText,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var bodyBlock = new TextBlock
        {
            Text              = $"{icon}  {body}",
            FontSize          = 12,
            Foreground        = ColMuted,
            TextWrapping      = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(8, 0, 8, 0),
        };

        var badge = new TextBlock
        {
            FontSize            = 11,
            FontWeight          = FontWeight.SemiBold,
            Text                = string.Empty,
            VerticalAlignment   = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        if (!liveBadges && index <= historicalCompletedThroughIndex)
        {
            badge.Text       = "✅  Completed";
            badge.Foreground = ColDoneBadgeFg;
        }

        Control? extrasChip = null;
        if (unit.Kind == TaskUnitKind.Prompt &&
            (unit.Attachments.Count > 0 || unit.PreloadedToolNames.Count > 0))
        {
            var chipRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            if (unit.Attachments.Count > 0)
                chipRow.Children.Add(new TextBlock
                {
                    Text       = $"📎{unit.Attachments.Count}",
                    FontSize   = 10,
                    Foreground = ColMuted,
                });
            if (unit.PreloadedToolNames.Count > 0)
                chipRow.Children.Add(new TextBlock
                {
                    Text       = $"🧩{unit.PreloadedToolNames.Count}",
                    FontSize   = 10,
                    Foreground = ColMuted,
                });
            extrasChip = chipRow;
            string tip = string.Join(", ",
                unit.Attachments.Select(System.IO.Path.GetFileName)
                    .Concat(unit.PreloadedToolNames));
            if (!string.IsNullOrEmpty(tip))
                ToolTip.SetTip(chipRow, tip);
        }

        var rowGrid = new Grid();
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        Grid.SetColumn(titleBlock, 0);
        Grid.SetColumn(bodyBlock, 1);
        if (extrasChip != null) Grid.SetColumn(extrasChip, 2);
        Grid.SetColumn(badge, 3);

        rowGrid.Children.Add(titleBlock);
        rowGrid.Children.Add(bodyBlock);
        if (extrasChip != null) rowGrid.Children.Add(extrasChip);
        rowGrid.Children.Add(badge);

        var rowBorder = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding      = new Thickness(10, 6, 10, 6),
            Background   = Brushes.Transparent,
            Child        = rowGrid,
        };

        if (!string.IsNullOrEmpty(unit.ChildSessionId))
        {
            string childId = unit.ChildSessionId;
            rowBorder.Cursor = new Cursor(StandardCursorType.Hand);
            ToolTip.SetTip(rowBorder, "Click to open this step's own chat");
            rowBorder.PointerEntered += (_, _) => rowBorder.Background = BgHoverItem;
            rowBorder.PointerExited  += (_, _) => rowBorder.Background = Brushes.Transparent;
            rowBorder.PointerPressed += (_, _) => OpenChildSessionFromTree(childId);
        }

        return (rowBorder, badge);
    }

    /// <summary>Appends ONE more row to a Task's ALREADY-BUILT</summary>
    private void AppendLiveTaskStepRow(ChatSession session, TaskUnit unit, int index)
    {
        var rowsPanel = session.Runtime.TaskStepsTreeRowsPanel;
        if (rowsPanel == null) return;

        var (rowBorder, badge) = BuildTaskStepRowControl(
            unit, index, liveBadges: true, historicalCompletedThroughIndex: -1);

        rowsPanel.Children.Add(rowBorder);
        session.Runtime.TaskStepBadges?.Add(badge);
        session.Runtime.TaskStepRows?.Add(rowBorder);

        RefreshTaskStepsTreeCountLabel(session);
    }

    /// <summary>Splices ONE more row into a Task's ALREADY-BUILT</summary>
    private void InsertLiveTaskStepRow(ChatSession session, TaskUnit unit, int index)
    {
        var rowsPanel = session.Runtime.TaskStepsTreeRowsPanel;
        if (rowsPanel == null) return;

        var (rowBorder, badge) = BuildTaskStepRowControl(
            unit, index, liveBadges: true, historicalCompletedThroughIndex: -1);

        if (index >= rowsPanel.Children.Count)
            rowsPanel.Children.Add(rowBorder);
        else
            rowsPanel.Children.Insert(index, rowBorder);

        var badges = session.Runtime.TaskStepBadges;
        var rows   = session.Runtime.TaskStepRows;
        if (badges != null)
        {
            if (index >= badges.Count) badges.Add(badge); else badges.Insert(index, badge);
        }
        if (rows != null)
        {
            if (index >= rows.Count) rows.Add(rowBorder); else rows.Insert(index, rowBorder);
        }

        RefreshTaskStepsTreeCountLabel(session);
    }

    /// <summary>Recomputes and repaints the live "Parent" tree's own "— N step(s) …" header sub-label</summary>
    private static void RefreshTaskStepsTreeCountLabel(ChatSession session)
    {
        var countLabel = session.Runtime.TaskStepsTreeCountLabel;
        if (countLabel == null) return;

        int promptCount = session.TaskUnits.Count(u => u.Kind == TaskUnitKind.Prompt);
        int waitCount    = session.TaskUnits.Count(u => u.Kind == TaskUnitKind.Wait);
        int chainCount   = session.TaskUnits.Count(u => u.Kind == TaskUnitKind.ChainTask);
        countLabel.Text = $"— {session.TaskUnits.Count} step(s)  ({promptCount} prompt, {waitCount} wait, {chainCount} chain)";
    }

    private (Border Banner, TextBlock StatusLabel, Button CancelButton, Button AddTaskButton, Button InsertNextButton, Button PauseButton) BuildTaskRunningBanner(ChatSession session)
    {
        var statusLabel = new TextBlock
        {
            FontSize          = 12,
            FontWeight        = FontWeight.SemiBold,
            Foreground        = ColScheduledBadgeFg,
            TextWrapping      = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Text              = "▶  Task starting…",
        };

        var insertNextBtn = new Button
        {
            Content    = "⤴  Insert Next",
            FontSize   = 12,
            FontWeight = FontWeight.SemiBold,
            Padding    = new Thickness(12, 6, 12, 6),
            Margin     = new Thickness(0, 0, 8, 0),
        };
        UiTheme.StyleButton(insertNextBtn, BgAiBubble, ColText,
            hoverBackground: BgHoverItem, pressedBackground: BgActiveItem, borderBrush: ColDivider);
        ToolTip.SetTip(insertNextBtn,
            "Insert a brand-new step immediately after the currently running step (shown as a " +
            "\"Sub-Task\") — it jumps ahead of any steps already waiting in the queue");
        insertNextBtn.Click += (_, _) => AddManualSubTaskStep(session);

        var addTaskBtn = new Button
        {
            Content    = "➕  Add Task",
            FontSize   = 12,
            FontWeight = FontWeight.SemiBold,
            Padding    = new Thickness(12, 6, 12, 6),
            Margin     = new Thickness(0, 0, 8, 0),
        };
        UiTheme.StyleButton(addTaskBtn, BgAiBubble, ColText,
            hoverBackground: BgHoverItem, pressedBackground: BgActiveItem, borderBrush: ColDivider);
        ToolTip.SetTip(addTaskBtn,
            "Manually append a brand-new step to the end of this running task — it will run " +
            "automatically, in order, once the sequence reaches it");
        addTaskBtn.Click += (_, _) => AddManualTaskStep(session);

        var pauseBtn = new Button
        {
            FontSize   = 12,
            FontWeight = FontWeight.SemiBold,
            Padding    = new Thickness(12, 6, 12, 6),
            Margin     = new Thickness(0, 0, 8, 0),
        };
        ToolTip.SetTip(pauseBtn,
            "Pauses / resumes the ENTIRE running task, including its Sub Agents. It stops at the end of the current " +
            "round (or between two steps), then everything is saved — you can close the app and continue later with ▶ Resume.");

        int lastPauseBtnState = -1;   // the 100 ms tick calls this; do not restyle a button that did not change

        void RefreshPauseBtnVisual()
        {
            bool requested = session.IsPaused;
            bool settled   = requested && IsPauseSettled(session);

            int state = !requested ? 0 : (settled ? 2 : 1);
            if (state == lastPauseBtnState) return;
            lastPauseBtnState = state;

            pauseBtn.Content   = state == 0 ? "⏸  Pause" : (state == 2 ? "▶  Resume" : "⏳  Pausing…");
            pauseBtn.IsEnabled = state != 1;   // "Pausing…" does nothing: wait until it is paused

            if (state == 0)
                UiTheme.StyleButton(pauseBtn, BgAiBubble, ColText,
                    hoverBackground: BgHoverItem, pressedBackground: BgActiveItem, borderBrush: ColDivider);
            else if (state == 2)
                UiTheme.StyleButton(pauseBtn, ColDoneBadgeBg, ColDoneBadgeFg);
            else
                UiTheme.StyleButton(pauseBtn, ColRetryAmber, Brushes.White,
                    disabledBackground: ColRetryAmber, disabledForeground: Brushes.White);
        }
        RefreshPauseBtnVisual();

        session.Runtime.TaskPauseButtonRefresh = RefreshPauseBtnVisual;

        pauseBtn.Click += (_, _) =>
        {
            if (!session.IsPaused)
                RequestPause(session);
            else if (IsPauseSettled(session))
                ResumeOwner(session);
            RefreshPauseBtnVisual();
        };

        var cancelBtn = new Button
        {
            Content    = "⏹  Cancel Task",
            FontSize   = 12,
            FontWeight = FontWeight.SemiBold,
            Padding    = new Thickness(12, 6, 12, 6),
        };
        UiTheme.StyleButton(cancelBtn, ColRunningRed, Brushes.White);
        ToolTip.SetTip(cancelBtn, "Immediately cancels the entire task — nothing further will run");
        cancelBtn.Click += (_, _) => CancelRunningTask(session);

        var statusRow = new Grid();
        statusRow.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        statusRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        statusRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        statusRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        statusRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        Grid.SetColumn(statusLabel,    0);
        Grid.SetColumn(insertNextBtn,  1);
        Grid.SetColumn(addTaskBtn,     2);
        Grid.SetColumn(pauseBtn,       3);
        Grid.SetColumn(cancelBtn,      4);
        statusRow.Children.Add(statusLabel);
        statusRow.Children.Add(insertNextBtn);
        statusRow.Children.Add(addTaskBtn);
        statusRow.Children.Add(pauseBtn);
        statusRow.Children.Add(cancelBtn);

        TextBlock? modelLockLabel = null;
        if (!string.IsNullOrEmpty(session.TaskBackendName))
        {
            modelLockLabel = new TextBlock
            {
                Text       = $"🔒  Model locked for this run: {session.TaskBackendName}",
                FontSize   = 11,
                Foreground = ColMuted,
            };
            ToolTip.SetTip(modelLockLabel,
                "Every step of this task uses this exact model, captured when the task started — " +
                "changing the header's LLM selector while this task is running has no effect on it.");
        }

        // Foldable "Parent" overview tree — live badges, since this IS the
        // running-task view (see BuildTaskStepsTree's own doc comment).
        var tree = BuildTaskStepsTree(session, session.TaskUnits, liveBadges: true);

        var outer = new StackPanel { Spacing = 4 };
        outer.Children.Add(statusRow);
        if (modelLockLabel != null) outer.Children.Add(modelLockLabel);
        outer.Children.Add(tree);

        var banner = new Border
        {
            Background   = ColScheduledBadgeBg,
            CornerRadius = new CornerRadius(8),
            Padding      = new Thickness(14, 10, 14, 10),
            Margin       = new Thickness(0, 0, 0, 8),
            Child        = outer,
        };

        return (banner, statusLabel, cancelBtn, addTaskBtn, insertNextBtn, pauseBtn);
    }

    private static void FinalizeTaskBanner(
        Border banner, TextBlock statusLabel, Button cancelBtn, Button addTaskBtn, Button insertNextBtn,
        Button pauseBtn, bool cancelled, bool hadError)
    {
        cancelBtn.IsEnabled = false;
        cancelBtn.IsVisible = false;

        addTaskBtn.IsEnabled = false;
        addTaskBtn.IsVisible = false;

        insertNextBtn.IsEnabled = false;
        insertNextBtn.IsVisible = false;

        // "⏸ Pause" / "▶ Resume" — nothing left to hold/release once the
        // task has stopped running for good either.
        pauseBtn.IsEnabled = false;
        pauseBtn.IsVisible = false;

        if (cancelled)
        {
            banner.Background      = ColRunningRed;
            statusLabel.Foreground = Brushes.White;
            statusLabel.Text       = "⏹  Task cancelled";
        }
        else if (hadError)
        {
            banner.Background      = ColRunningRed;
            statusLabel.Foreground = Brushes.White;
            statusLabel.Text       = "✗  Task failed";
        }
        else
        {
            banner.Background      = ColDoneBadgeBg;
            statusLabel.Foreground = ColDoneBadgeFg;
            statusLabel.Text       = "✅  Task complete";
        }
    }

    /// <summary>Build the "📋 Task Blueprint" placeholder shown in the main chat area for a Task session that has never been run yet</summary>
    private Control BuildTaskBlueprintPlaceholder(ChatSession session)
    {
        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
            Margin              = new Thickness(0, 40, 0, 0),
            Spacing             = 10,
            MaxWidth            = 560,
            MinWidth            = 480,
        };

        panel.Children.Add(new TextBlock
        {
            Text                = "📋",
            FontSize            = 48,
            Foreground          = ColScheduledAccent,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        panel.Children.Add(new TextBlock
        {
            Text                = "Task Blueprint",
            FontSize            = 22,
            FontWeight          = FontWeight.SemiBold,
            Foreground          = ColText,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        panel.Children.Add(new TextBlock
        {
            Text                = $"{session.TaskUnits.Count} step(s) — will run in order, 1 → 2 → 3 → …",
            FontSize            = 13,
            Foreground          = ColMuted,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        // Foldable "Parent" overview tree — no live badges (task hasn't run).
        var tree = BuildTaskStepsTree(session, session.TaskUnits, liveBadges: false);
        panel.Children.Add(tree);

        var btnRow = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            Spacing             = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin              = new Thickness(0, 6, 0, 0),
        };

        var runBtn = new Button
        {
            Content    = "▶  Run Task",
            FontSize   = 13,
            FontWeight = FontWeight.SemiBold,
            Padding    = new Thickness(16, 8, 16, 8),
        };
        UiTheme.StyleButton(runBtn, ColAccent, Brushes.White);
        ToolTip.SetTip(runBtn, "Start running this blueprint now, step 1 first");
        runBtn.Click += (_, _) => RunExistingTask(session, session.TaskUnits);

        var editBtn = new Button
        {
            Content    = "✎  Edit Blueprint",
            FontSize   = 13,
            FontWeight = FontWeight.SemiBold,
            Padding    = new Thickness(16, 8, 16, 8),
        };
        UiTheme.StyleButton(editBtn, BgAiBubble, ColText,
            hoverBackground: BgHoverItem, pressedBackground: BgActiveItem, borderBrush: ColDivider);
        editBtn.Click += (_, _) => OpenTaskBuilderWindowForEdit(session);

        var deleteBtn = new Button
        {
            Content    = "✕  Delete",
            FontSize   = 13,
            FontWeight = FontWeight.SemiBold,
            Padding    = new Thickness(16, 8, 16, 8),
        };
        UiTheme.StyleButton(deleteBtn, ColRunningRed, Brushes.White);
        ToolTip.SetTip(deleteBtn, "Delete this blueprint and remove this chat");
        deleteBtn.Click += (_, _) => DeleteTaskBlueprint(session);

        btnRow.Children.Add(runBtn);
        btnRow.Children.Add(editBtn);
        btnRow.Children.Add(deleteBtn);
        panel.Children.Add(btnRow);

        return panel;
    }

    /// <summary>Build the "⏰ Scheduled Task" placeholder shown in the main chat area for a Task session that is scheduled to auto-run</summary>
    private Control BuildTaskScheduledPlaceholder(ChatSession session)
    {
        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
            Margin              = new Thickness(0, 40, 0, 0),
            Spacing             = 10,
            MaxWidth            = 560,
            MinWidth            = 480,
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
            Text                = "Scheduled Task",
            FontSize            = 22,
            FontWeight          = FontWeight.SemiBold,
            Foreground          = ColText,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        string whenText = session.TaskScheduledAt.HasValue
            ? session.TaskScheduledAt.Value.ToString("dddd, MMM d  •  HH:mm")
            : "—";

        panel.Children.Add(new TextBlock
        {
            Text                = $"{session.TaskUnits.Count} step(s) — starts at {whenText}",
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
        if (session.TaskScheduledAt.HasValue)
            SetCountdownText(countdownLabel, session.TaskScheduledAt.Value);
        session.Runtime.TaskScheduledCountdownLabel = countdownLabel;
        panel.Children.Add(countdownLabel);

        // Foldable "Parent" overview tree — no live badges (nothing has
        // started running yet).
        var tree = BuildTaskStepsTree(session, session.TaskUnits, liveBadges: false);
        panel.Children.Add(tree);

        var btnRow = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            Spacing             = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin              = new Thickness(0, 6, 0, 0),
        };

        var startNowBtn = new Button
        {
            Content    = "▶  Start Now",
            FontSize   = 13,
            FontWeight = FontWeight.SemiBold,
            Padding    = new Thickness(16, 8, 16, 8),
        };
        UiTheme.StyleButton(startNowBtn, ColAccent, Brushes.White);
        ToolTip.SetTip(startNowBtn, "Start running this blueprint right now, without waiting for the scheduled time");
        startNowBtn.Click += (_, _) => FireScheduledTask(session);

        var editBtn = new Button
        {
            Content    = "✎  Edit / Reschedule",
            FontSize   = 13,
            FontWeight = FontWeight.SemiBold,
            Padding    = new Thickness(16, 8, 16, 8),
        };
        UiTheme.StyleButton(editBtn, BgAiBubble, ColText,
            hoverBackground: BgHoverItem, pressedBackground: BgActiveItem, borderBrush: ColDivider);
        ToolTip.SetTip(editBtn, "Edit the blueprint's steps and/or change the scheduled date/time");
        editBtn.Click += (_, _) => OpenTaskBuilderWindowForEdit(session);

        var cancelBtn = new Button
        {
            Content    = "✕  Cancel",
            FontSize   = 13,
            FontWeight = FontWeight.SemiBold,
            Padding    = new Thickness(16, 8, 16, 8),
        };
        UiTheme.StyleButton(cancelBtn, ColRunningRed, Brushes.White);
        ToolTip.SetTip(cancelBtn, "Cancel this scheduled task and remove this chat");
        cancelBtn.Click += (_, _) => DeleteTaskBlueprint(session);

        btnRow.Children.Add(startNowBtn);
        btnRow.Children.Add(editBtn);
        btnRow.Children.Add(cancelBtn);
        panel.Children.Add(btnRow);

        return panel;
    }

    /// <summary>Build the static "📋 Task" overview shown in the main chat area for a Task session that HAS already run</summary>
    private Control BuildTaskFinishedOverviewPlaceholder(ChatSession session)
    {
        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
            Margin              = new Thickness(0, 40, 0, 0),
            Spacing             = 10,
            MaxWidth            = 560,
            MinWidth            = 480,
        };

        panel.Children.Add(new TextBlock
        {
            Text                = "📋",
            FontSize            = 48,
            Foreground          = ColScheduledAccent,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        panel.Children.Add(new TextBlock
        {
            Text                = "Task",
            FontSize            = 22,
            FontWeight          = FontWeight.SemiBold,
            Foreground          = ColText,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        int historicalThrough = ComputeHistoricalCompletedThroughIndex(session.TaskUnits);
        bool fullyDone         = session.TaskUnits.Count > 0 && historicalThrough >= session.TaskUnits.Count - 1;

        panel.Children.Add(new TextBlock
        {
            Text = fullyDone
                ? $"{session.TaskUnits.Count} step(s) — completed in a previous session."
                : $"{session.TaskUnits.Count} step(s) — ran partially in a previous session " +
                  "(the app may have been closed before every step fired).",
            FontSize            = 13,
            Foreground          = ColMuted,
            TextWrapping        = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment       = TextAlignment.Center,
        });

        if (!string.IsNullOrEmpty(session.TaskBackendName))
        {
            panel.Children.Add(new TextBlock
            {
                Text                = $"🔒  Ran on: {session.TaskBackendName}",
                FontSize            = 11,
                Foreground          = ColMuted,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
        }

        var tree = BuildTaskStepsTree(session, session.TaskUnits, liveBadges: false, historicalThrough);
        panel.Children.Add(tree);

        if (!fullyDone && session.TaskUnits.Count > 0)
        {
            var resumeBtn = new Button
            {
                Content              = "▶  Resume Task",
                FontSize             = 13,
                FontWeight           = FontWeight.SemiBold,
                Padding              = new Thickness(16, 8, 16, 8),
                HorizontalAlignment  = HorizontalAlignment.Center,
                Margin               = new Thickness(0, 6, 0, 0),
            };
            UiTheme.StyleButton(resumeBtn, ColAccent, Brushes.White);
            ToolTip.SetTip(resumeBtn,
                "Continues running this blueprint from right after the last step that actually " +
                "finished — nothing already completed is re-sent.");
            resumeBtn.Click += (_, _) => ResumeInterruptedTask(session);
            panel.Children.Add(resumeBtn);
        }

        panel.Children.Add(new TextBlock
        {
            Text                = "Click any step above to open its own chat.",
            FontSize            = 11,
            Foreground          = ColMuted,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin              = new Thickness(0, 4, 0, 0),
        });

        return panel;
    }

    /// <summary>Best-effort reconstruction of "how far did this task get" purely from PERSISTED data</summary>
    private static int ComputeHistoricalCompletedThroughIndex(List<TaskUnit> units)
    {
        int result = -1;
        for (int i = 0; i < units.Count; i++)
        {
            var u = units[i];
            if (u.Kind == TaskUnitKind.Prompt && !string.IsNullOrEmpty(u.ChildSessionId))
                result = i;
        }
        return result;
    }

    private static string TruncateOneLine(string text, int maxLen)
    {
        string oneLine = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return oneLine.Length > maxLen ? oneLine[..maxLen] + "…" : oneLine;
    }

    private static string FormatWaitSecondsShort(double seconds)
    {
        if (seconds >= 3600) return $"{seconds / 3600.0:0.##}h";
        if (seconds >= 60)   return $"{seconds / 60.0:0.##}m";
        return $"{seconds:0.##}s";
    }
}
