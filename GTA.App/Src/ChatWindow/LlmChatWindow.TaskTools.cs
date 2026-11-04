using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Avalonia.Threading;

namespace GTA.App;

public partial class LlmChatWindow
{

    internal string GetTaskStatusReportForCallingSession()
    {
        var callingSession = DelegateTaskContext.CurrentSession;
        if (callingSession == null)
        {
            return "Error: task_status could not determine which session is calling it " +
                   "(no active session context) — this should not normally happen.";
        }

        string? parentId = callingSession.ParentTaskId;
        if (string.IsNullOrEmpty(parentId))
        {
            return "Error: task_status can only be used from a step that is itself running " +
                   "as part of a Task blueprint (see the '📋 Task' feature) — this chat is not " +
                   "one of those, so there is no running Task to report on.";
        }

        using var doneSignal = new ManualResetEventSlim(false);
        string result = string.Empty;

        void DoWork()
        {
            var parentTask = _sessions.FirstOrDefault(s => s.Id == parentId && s.IsTask);
            result = parentTask == null
                ? "Error: the parent Task session could not be found (it may have been deleted)."
                : BuildTaskStatusReport(parentTask);
            doneSignal.Set();
        }

        if (Dispatcher.UIThread.CheckAccess())
            DoWork();
        else
            Dispatcher.UIThread.Post(DoWork);

        doneSignal.Wait();
        return result;
    }

    private static string BuildTaskStatusReport(ChatSession parentTask)
    {
        var rt     = parentTask.Runtime;
        int curIdx = rt.TaskCurrentUnitIndex;

        var sb = new StringBuilder();
        sb.AppendLine($"📋 Task status — '{parentTask.Title}' — {parentTask.TaskUnits.Count} step(s):");

        for (int j = 0; j < parentTask.TaskUnits.Count; j++)
        {
            var unit = parentTask.TaskUnits[j];

            string state;
            if (unit.IsSupersededByRestart)
                state = "OLD";
            else if (j == curIdx)
                state = "RUNNING";
            else if (rt.TaskRestartingIndices.Contains(j))
                state = "RESTARTING";
            else if (curIdx >= 0 && j < curIdx)
                state = "DONE";
            else if (curIdx < 0 && !string.IsNullOrEmpty(unit.ChildSessionId))
                state = "DONE";
            else
                state = "WAITING";

            string label = unit.SubTaskNumber > 0
                ? $"Sub-Task {unit.SubTaskNumber}"
                : unit.RestartLabelIndex >= 0
                    ? $"Task {unit.RestartLabelIndex + 1}"
                    : $"Task {j + 1}";

            sb.AppendLine($"[{j}] ({state}) {label}: {DescribeTaskUnit(unit)}");
        }

        return sb.ToString();
    }

    private static string DescribeTaskUnit(TaskUnit unit) => unit.Kind switch
    {
        TaskUnitKind.Prompt    => TruncateOneLine(unit.PromptText, 90),
        TaskUnitKind.Wait      => unit.WaitSummary,
        TaskUnitKind.ChainTask => $"Chain after: {unit.ChainTaskSessionTitle ?? "(unknown task)"}",
        _                      => string.Empty,
    };

    internal string RestartTaskStepFromToolCall(int targetIndex, string injectionMessage)
    {
        var callingSession = DelegateTaskContext.CurrentSession;
        if (callingSession == null)
        {
            return "Error: restart_task could not determine which session is calling it " +
                   "(no active session context) — this should not normally happen.";
        }

        string? parentId = callingSession.ParentTaskId;
        if (string.IsNullOrEmpty(parentId))
        {
            return "Error: restart_task can only be used from a step that is itself running " +
                   "as part of a Task blueprint (see the '📋 Task' feature) — this chat is not " +
                   "one of those, so there is no running Task to restart a step of.";
        }

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

            var rt     = parentTask.Runtime;
            int curIdx = rt.TaskCurrentUnitIndex;

            if (curIdx < 0)
            {
                result = "Error: the parent Task is not currently running (it already finished, was " +
                         "cancelled, or never started) — restart_task only works while it is actively " +
                         "executing.\n\n" + BuildTaskStatusReport(parentTask);
                doneSignal.Set();
                return;
            }

            if (targetIndex < 0 || targetIndex >= parentTask.TaskUnits.Count)
            {
                result = $"Error: 'index' {targetIndex} is out of range — this Task has " +
                         $"{parentTask.TaskUnits.Count} step(s) (valid indices: 0 to " +
                         $"{parentTask.TaskUnits.Count - 1}).\n\n" + BuildTaskStatusReport(parentTask);
                doneSignal.Set();
                return;
            }

            if (targetIndex >= curIdx)
            {
                result = $"Error: index {targetIndex} is not a DONE step — it is either the currently " +
                         "RUNNING step or still WAITING (a future step). restart_task can only target a " +
                         "step that has ALREADY completed, strictly before the currently running step." +
                         "\n\n" + BuildTaskStatusReport(parentTask);
                doneSignal.Set();
                return;
            }

            var targetUnit = parentTask.TaskUnits[targetIndex];
            if (targetUnit.Kind != TaskUnitKind.Prompt)
            {
                result = $"Error: index {targetIndex} is a {targetUnit.Kind} step, not a Prompt step — " +
                         "only a completed Prompt step can be restarted.\n\n" + BuildTaskStatusReport(parentTask);
                doneSignal.Set();
                return;
            }

            if (targetUnit.IsSupersededByRestart)
            {
                result = $"Error: index {targetIndex} was already superseded by a previous restart " +
                         "([OLD]) — it cannot be targeted again. Its own replay (further down the " +
                         "blueprint) is a perfectly valid target instead, once IT has completed.\n\n" +
                         BuildTaskStatusReport(parentTask);
                doneSignal.Set();
                return;
            }

            targetUnit.IsSupersededByRestart = true;
            MarkTaskStepOld(parentTask, targetIndex);

            int insertIndex = targetIndex + 1;
            var replacement = new TaskUnit
            {
                Kind               = TaskUnitKind.Prompt,
                PromptText         = targetUnit.PromptText,
                Attachments        = new List<string>(targetUnit.Attachments),
                PreloadedToolNames = new List<string>(targetUnit.PreloadedToolNames),
                RestartLabelIndex  = targetIndex,
            };
            InsertRestartReplacementUnit(parentTask, replacement, insertIndex);

            for (int origIdx = targetIndex + 1; origIdx < curIdx; origIdx++)
            {
                int shifted = origIdx + 1;
                rt.TaskRestartingIndices.Add(shifted);
                MarkTaskStepRestarting(parentTask, shifted);
            }

            rt.TaskRestartingIndices.Add(insertIndex);
            MarkTaskStepRestarting(parentTask, insertIndex);

            rt.TaskRestartPendingIndexShift = insertIndex;
            rt.TaskRestartRequested         = true;
            rt.TaskRestartTargetIndex       = insertIndex;
            rt.TaskRestartInjectionMessage  = injectionMessage;

            ConversationPersistence.SaveSession(parentTask);

            result = $"✅ Restart scheduled — Task {targetIndex + 1}'s own completed result is now " +
                     "frozen and marked [OLD]; a fresh replay of it (reusing the same 'Task " +
                     $"{targetIndex + 1}' label) has been inserted right after it and will run once " +
                     "the currently running step finishes. Your injection_message will be delivered " +
                     "to it the moment it starts, exactly like a normal 💉 message injection. Every " +
                     "step in between (including the one currently running) will ALSO re-run " +
                     "afterward, in order, in its own original slot — so it genuinely gets a fresh " +
                     "chance to re-verify the new result, never silently skipped. restart_task is not " +
                     "capped at one use per run — once this replay (or any later step) completes, it " +
                     "may call restart_task again too.\n\n" + BuildTaskStatusReport(parentTask);
            doneSignal.Set();
        }

        if (Dispatcher.UIThread.CheckAccess())
            DoWork();
        else
            Dispatcher.UIThread.Post(DoWork);

        doneSignal.Wait();
        return result;
    }

    internal string ForwardReportFromToolCall(string message)
    {
        var callingSession = DelegateTaskContext.CurrentSession;
        if (callingSession == null)
        {
            return "Error: forward_report could not determine which session is calling it " +
                   "(no active session context) — this should not normally happen.";
        }

        string? parentId = callingSession.ParentTaskId;
        if (string.IsNullOrEmpty(parentId))
        {
            return "Error: forward_report can only be used from a step that is itself running " +
                   "as part of a Task blueprint (see the '📋 Task' feature) — this chat is not " +
                   "one of those, so there is no running Task to forward a message within.";
        }

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

            var rt = parentTask.Runtime;
            if (rt.TaskCurrentUnitIndex < 0)
            {
                result = "Error: the parent Task is not currently running (it already finished, was " +
                         "cancelled, or never started) — forward_report only works while it is actively " +
                         "executing.\n\n" + BuildTaskStatusReport(parentTask);
                doneSignal.Set();
                return;
            }

            rt.TaskForwardMessageQueue.Add(message);

            result = "✅ Forwarded — this message will be delivered as a 💉 message injection the " +
                     "instant the NEXT Prompt step of this Task actually starts running (whichever " +
                     "step that ends up being — a 'Wait'/'⛓ Chain Task' step in between is skipped " +
                     "over automatically, since it never spawns its own child session). Not a one-shot: " +
                     "calling forward_report again before that happens simply queues another separate " +
                     "message, delivered in the same order they were sent.\n\n" + BuildTaskStatusReport(parentTask);
            doneSignal.Set();
        }

        if (Dispatcher.UIThread.CheckAccess())
            DoWork();
        else
            Dispatcher.UIThread.Post(DoWork);

        doneSignal.Wait();
        return result;
    }
}
