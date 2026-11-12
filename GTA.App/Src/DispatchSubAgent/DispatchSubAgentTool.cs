using System.Text.Json.Nodes;
using GTA.Tools;

namespace GTA.App;

internal static class DispatchSubAgentTool
{
    [AvaloniaOnlyTool(
        "dispatch_sub_agent",
        "Dispatch an isolated Sub Agent (same model, a completely fresh, focused train of thought) " +
        "to accomplish EXACTLY ONE self-contained task on your behalf, then report back. The sub " +
        "agent sees this conversation's history up to (but NOT including) this current round, plus " +
        "every tool you currently have available EXCEPT dispatch_sub_agent itself (it cannot dispatch " +
        "further sub agents). It then runs its own private, independent sequence of thinking/tool " +
        "calls — completely invisible to, and never touching, this conversation's own history — until " +
        "it ends by reporting a status ('success' or 'failure') and a message. ONLY that final report " +
        "(never anything else it did along the way) comes back to you, as this tool's own result — " +
        "everything else it did is discarded, exactly as if it never happened. Use this to explore a " +
        "side-quest, verify something, or delegate a self-contained chunk of work without bloating or " +
        "derailing your own conversation with all of the intermediate steps. By default the sub " +
        "agent CANNOT use a shell/arbitrary-command tool, CANNOT mutate (create/modify/delete/move) " +
        "any file or folder, and can NEVER call dispatch_sub_agent/delegate_task/ask_questions — see " +
        "'allow_use_shell'/'allow_file_mutation' below to loosen the first two; the last one can " +
        "never be loosened. " +
        "PARALLEL EXECUTION: if you call dispatch_sub_agent SEVERAL times in the SAME response (several " +
        "tool calls in one message), ALL of those Sub Agents run AT THE SAME TIME, and you get every " +
        "report back together once ALL of them have finished (success or failure). The total time is " +
        "about the time of the slowest one, not the sum. Use this for independent tasks (for example: " +
        "investigate 3 different folders, check 3 unrelated questions). At most " +
        "4 run at once; extra ones wait for a free slot and still run. Rules for parallel use: " +
        "(1) every task must be fully independent — a Sub Agent can NOT see the result or the work of " +
        "another Sub Agent of the same batch, and it cannot see the other tool calls of this response; " +
        "(2) never give two parallel Sub Agents 'allow_file_mutation: true' on the same file or folder — " +
        "they would overwrite each other; (3) one Sub Agent failing does not stop the others — each " +
        "report has its own SUCCESS/FAILURE status, so read every one; (4) each Sub Agent copies the " +
        "whole conversation, so N parallel Sub Agents cost about N times the tokens — only parallelize " +
        "when it really saves time; (5) if task B needs the result of task A, do NOT put them in the " +
        "same response — dispatch A first, read its report, then dispatch B.",
        Tags = "interactive")]
    [Parameter(index: 0, name: "task",
        type: "string",
        description: "The exact task/instructions the Sub Agent must accomplish — write it as a " +
                     "complete, self-contained assignment with all the context it will need. It " +
                     "cannot ask you anything back, so make it unambiguous.",
        required: true)]
    [Parameter(index: 1, name: "allow_use_shell",
        type: "boolean",
        description: "Optional, defaults to false. When false (the default), the Sub Agent is denied " +
                     "(with a plain explanatory string, before the call ever actually runs) any tool " +
                     "that uses a shell/arbitrary-command-execution capability (currently: run_shell). " +
                     "Pass true only if this task genuinely needs that.",
        required: false)]
    [Parameter(index: 2, name: "allow_file_mutation",
        type: "boolean",
        description: "Optional, defaults to false. When false (the default), the Sub Agent is denied " +
                     "(with a plain explanatory string, before the call ever actually runs) any tool " +
                     "that creates, modifies, deletes, or moves a file or folder on the local file " +
                     "system (write_file, edit_line, insert_line, remove_line, delete_file, copy_file, " +
                     "move_file, git_commit, xlsx_set_cell_values, pptx_* editors, dotnet_build, etc.). " +
                     "Pass true only if this task genuinely needs to create/modify files on disk.",
        required: false)]
    public static object DispatchSubAgent(JsonObject input)
    {
        return "Error: dispatch_sub_agent must be executed through the normal agentic loop " +
               "(GTA.Builder.AgenticLoop intercepts this tool call before it would ever reach " +
               "this fallback) — seeing this message means something is misconfigured.";
    }
}
