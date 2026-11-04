using System.Text.Json.Nodes;
using GTA.Tools;

namespace GTA.App;

internal static class ForwardReportTool
{
    [AvaloniaOnlyTool(
        "forward_report",
        "Send a free-text message FORWARD from the step currently running in the SAME Task " +
        "blueprint you are already part of (see the '📋 Task' feature) to whichever step runs " +
        "NEXT. Delivered to that next step's own brand-new child session the instant IT starts " +
        "— exactly like a normal 💉 message injection (a separate injected note, NOT appended to " +
        "or replacing that step's own original prompt text). There is deliberately NO 'index' " +
        "parameter: 'next' is resolved lazily, at the moment the next step actually starts, so it " +
        "automatically lands correctly even if a 'Wait'/'⛓ Chain Task' step (which never spawns " +
        "its own child session) sits in between, or delegate_task/restart_task splices extra " +
        "step(s) in first — whichever step genuinely ends up running next receives it. Calling " +
        "this more than once before that happens simply queues multiple separate messages, " +
        "delivered in the order they were sent — not capped at one use per run. Every call " +
        "(successful or not) always returns the full current task_status report alongside its " +
        "own result.",
        Tags = "interactive")]
    [Parameter(index: 0, name: "message",
        type: "string",
        description: "Free-text note to deliver to whichever step runs NEXT in this Task blueprint, " +
                     "the moment it starts — exactly like a normal 💉 message injection. That step's " +
                     "own original prompt text is still sent unchanged alongside this.",
        required: true)]
    public static object ForwardReport(JsonObject input)
    {
        var window = LlmChatWindow.Instance;
        if (window == null)
            return "Error: forward_report requires the Avalonia chat UI to be running, but no window instance was found.";

        string message = input["message"]?.GetValue<string>() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(message))
            return "Error: 'message' must be a non-empty string.";

        return window.ForwardReportFromToolCall(message.Trim());
    }
}
