using System.Text.Json.Nodes;

namespace GTA.App;

internal static class TaskStatusTool
{
    [AvaloniaOnlyTool(
        "task_status",
        "Report the live status of every step in the SAME running Task blueprint you are currently " +
        "executing as one step's own child session (see the '📋 Task' feature) — calling this from an " +
        "ordinary chat, or a Task that isn't currently running, returns an error explaining why. Returns " +
        "one line per step, 0-based index first, e.g.: '[0] (OLD) Task 1: CHECK', '[1] (DONE) Task 2: " +
        "IMPL', '[2] (RESTARTING) Task 1: CHECK', '[3] (RUNNING) Task 3: REVIEW…', '[4] (WAITING) Task " +
        "4: …'. (OLD) means a restart_task call has permanently superseded that step — its result is " +
        "frozen/historical, and a fresh replay of it (reusing the SAME 'Task N' label) now exists " +
        "further down the list as the current, valid version instead. Takes no parameters — it always " +
        "reports on the ONE Task blueprint your own currently-running step belongs to; other Task " +
        "blueprints are out of scope.",
        Tags = "interactive")]
    public static object TaskStatus(JsonObject input)
    {
        var window = LlmChatWindow.Instance;
        if (window == null)
            return "Error: task_status requires the Avalonia chat UI to be running, but no window instance was found.";

        return window.GetTaskStatusReportForCallingSession();
    }
}
