using System.Text.Json.Nodes;
using GTA.Tools;

namespace GTA.App;

internal static class RestartTaskTool
{
    [AvaloniaOnlyTool(
        "restart_task",
        "Restart ONE already-completed (DONE) step of the SAME running Task blueprint you are " +
        "currently executing as one step's own child session (see the '📋 Task' feature) FROM " +
        "SCRATCH. The target's own completed result is frozen permanently and marked [OLD] (never " +
        "touched again); a fresh replay of it — reusing its ORIGINAL prompt text and its ORIGINAL " +
        "'Task N' label — is inserted right after it and will run once the currently running step " +
        "finishes. ONLY that replay receives 'injection_message' — delivered exactly like a normal " +
        "💉 message injection (a separate injected note, NOT appended to or replacing the original " +
        "prompt) the moment it starts, so it knows what to do differently this time. Every step " +
        "between the target and the currently running one (INCLUDING the currently running one " +
        "itself) will ALSO re-run afterward, in its own original slot, once the replay chain reaches " +
        "it — so e.g. a 'snippet -> check' loop genuinely gets a fresh check of the corrected result, " +
        "never silently skipped. 'index' MUST refer to a step that is already DONE (strictly before " +
        "the currently running step) and not already superseded by an earlier restart ([OLD]) — you " +
        "cannot target the currently RUNNING step, a still-WAITING (future) step, or an [OLD] step; " +
        "this only ever applies to the ONE local Task blueprint you are already part of. restart_task " +
        "is NOT capped at one use per run — a replay (once it is itself running, or later completes) " +
        "may call restart_task again too, e.g. for a multi-pass 'keep fixing until satisfied' loop. " +
        "Every call (successful or not) always returns the full current task_status report alongside " +
        "its own result.",
        Tags = "interactive")]
    [Parameter(index: 0, name: "index",
        type: "integer",
        description: "0-based index (matching task_status's own [N] numbering) of the already-DONE, " +
                     "not-yet-[OLD] step to restart from scratch. Must be strictly less than the " +
                     "index of the currently running step.",
        required: true)]
    [Parameter(index: 1, name: "injection_message",
        type: "string",
        description: "Free-text note delivered to the restarted step's brand-new replay the moment " +
                     "it starts running — exactly like a normal 💉 message injection — telling it " +
                     "what to do differently this time. Its original prompt text is still sent " +
                     "unchanged alongside this.",
        required: true)]
    public static object RestartTask(JsonObject input)
    {
        var window = LlmChatWindow.Instance;
        if (window == null)
            return "Error: restart_task requires the Avalonia chat UI to be running, but no window instance was found.";

        int targetIndex;
        try
        {
            targetIndex = input["index"]!.GetValue<int>();
        }
        catch
        {
            return "Error: 'index' must be a valid integer.";
        }

        string injectionMessage = input["injection_message"]?.GetValue<string>() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(injectionMessage))
            return "Error: 'injection_message' must be a non-empty string.";

        return window.RestartTaskStepFromToolCall(targetIndex, injectionMessage.Trim());
    }
}
