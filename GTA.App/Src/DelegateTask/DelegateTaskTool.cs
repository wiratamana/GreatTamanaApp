using System;
using System.Text.Json.Nodes;
using GTA.Tools;

namespace GTA.App;

internal static class DelegateTaskTool
{
    [AvaloniaOnlyTool(
        "delegate_task",
        "Delegate a brand-new task step to the SAME running Task blueprint you are currently " +
        "executing as one step's own child session (see the '📋 Task' feature) — calling this from " +
        "an ordinary chat, or a Task that isn't currently running, returns an error explaining why. " +
        "Two placement modes via 'position': 'end' (default) appends it to the END, after every " +
        "already-queued step — e.g. if the sidebar currently shows Task 1..7, this creates Task 8. " +
        "'next' instead splices it in IMMEDIATELY AFTER the step that is CURRENTLY RUNNING (shown as " +
        "a 'Sub-Task'), jumping ahead of anything already queued — use this when the new step is an " +
        "urgent follow-up that must happen before the rest of the blueprint continues. Calling this " +
        "multiple times with 'next' in the same turn stacks each one after the last, preserving your " +
        "call order. The new step inherits the same pre-loaded tools THIS step currently has " +
        "(including delegate_task itself, if you have it), so it can keep delegating further steps " +
        "recursively if it needs to.",
        Tags = "interactive")]
    [Parameter(index: 0, name: "prompt",
        type: "string",
        description: "The exact prompt text the new delegated task step should send to the LLM " +
                     "once it runs — write it exactly like a normal user message/instruction, " +
                     "with all context it will need (it starts a brand-new chat with no memory of " +
                     "this conversation).",
        required: true)]
    [Parameter(index: 1, name: "position",
        type: "string",
        description: "Where to place the new step: 'end' (default) appends it to the end of the " +
                     "blueprint, after every already-queued step. 'next' splices it in immediately " +
                     "after the currently-running step instead, ahead of anything already queued.",
        required: false)]
    public static object DelegateTask(JsonObject input)
    {
        var window = LlmChatWindow.Instance;
        if (window == null)
            return "Error: delegate_task requires the Avalonia chat UI to be running, but no window instance was found.";

        string prompt = input["prompt"]?.GetValue<string>() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(prompt))
            return "Error: 'prompt' must be a non-empty string.";

        string position = input["position"]?.GetValue<string>() ?? "end";
        if (!string.Equals(position, "end", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(position, "next", StringComparison.OrdinalIgnoreCase))
        {
            return "Error: 'position' must be either 'end' or 'next' (got: '" + position + "').";
        }

        return window.DelegateNewTaskStep(prompt.Trim(), position);
    }
}
