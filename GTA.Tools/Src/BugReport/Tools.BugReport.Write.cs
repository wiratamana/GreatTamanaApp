using System;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using GTA.Tools.Impl.BugReport;

namespace GTA.Tools
{
    internal static partial class ToolsBugReport
    {
        [ToolTag("essential", "bootstrap", "bug_report")]
        [FileMutating]
        [ToolInfo(
            name: "bug_report",
            description:
                "Report a MALFUNCTIONING TOOL — and ONLY a malfunctioning tool. Call this exclusively " +
                "when a tool you actually invoked during this task errored, threw an exception, timed " +
                "out, or returned an obviously wrong/unexpected/garbage result. Writes one plain-text " +
                "file named '{date_now}_{title}.md' (today's date plus a sanitized version of the " +
                "title) inside the shared 'BugReports' folder next to the running executable — no LLM " +
                "call is made, this is a pure local file write.\n\n" +
                "DO NOT call this tool for anything else: not for general ideas, improvement notes, " +
                "process gaps, or observations unrelated to a tool actually misbehaving. If nothing " +
                "anomalous happened with any tool you used during this task, simply do nothing — do " +
                "not call bug_report 'just in case'. If you do notice something worth mentioning that " +
                "is NOT a tool malfunction, just mention it briefly in your final summary to the user " +
                "instead of logging it here.\n\n" +
                "Typical correct workflow: a tool call fails or misbehaves → you retry with different " +
                "parameters to confirm it is genuinely broken (not just a one-off mistake in how you " +
                "called it) → only then call bug_report with the exact failing parameters and error " +
                "text (sanitized of secrets/tokens/credentials) as 'evidence', so a developer can " +
                "reproduce and fix it.")]
        [Parameter(index: 0, name: "title",
            type: "string",
            description: "Short title for this bug report (used to build the file name together with " +
                         "today's date, e.g. 'Box_ReadXlsx_Timeout_On_Large_Files'). Keep it short and descriptive.",
            required: true)]
        [Parameter(index: 1, name: "tool_name",
            type: "string",
            description: "The exact name of the tool that malfunctioned, e.g. 'box_read_xlsx' or 'dotnet_build'.",
            required: true)]
        [Parameter(index: 2, name: "issue",
            type: "string",
            description: "The background and actual details of what went wrong: what you were trying to do, " +
                         "what you expected the tool to do, and what it actually did instead. Be precise and " +
                         "factual.",
            required: true)]
        [Parameter(index: 3, name: "evidence",
            type: "string",
            description: "The actual evidence captured when the failure happened: the exact parameters you " +
                         "passed to the tool and the exact error/wrong output text it returned. SANITIZE " +
                         "secrets first (API keys, tokens, passwords, cookies, personal data) — replace them " +
                         "with a placeholder like '[REDACTED]' before including them here.",
            required: true)]
        [Parameter(index: 4, name: "task",
            type: "string",
            description: "A concrete to-do task for the developer who will fix this tool — what should be " +
                         "changed (e.g. fix a bug, correct a default value, improve validation/error " +
                         "handling, update the tool description) so this stops happening.",
            required: true)]
        public static object BugReport(JsonObject input)
        {
            string? title     = input["title"]?.GetValue<string>();
            string? toolName  = input["tool_name"]?.GetValue<string>();
            string? issue     = input["issue"]?.GetValue<string>();
            string? evidence  = input["evidence"]?.GetValue<string>();
            string? task      = input["task"]?.GetValue<string>();

            if (string.IsNullOrWhiteSpace(title))
                return "Error: 'title' parameter is missing or empty.";
            if (string.IsNullOrWhiteSpace(toolName))
                return "Error: 'tool_name' parameter is missing or empty.";
            if (string.IsNullOrWhiteSpace(issue))
                return "Error: 'issue' parameter is missing or empty.";
            if (string.IsNullOrWhiteSpace(evidence))
                return "Error: 'evidence' parameter is missing or empty.";
            if (string.IsNullOrWhiteSpace(task))
                return "Error: 'task' parameter is missing or empty.";

            DateTime now = DateTime.Now;
            string   path;

            try
            {
                path = BugReportStore.BuildEntryPath(title, now);

                string content =
                    $"# {title.Trim()}\n" +
                    $"Date: {now:yyyy-MM-dd HH:mm:ss}\n" +
                    $"Tool: {toolName.Trim()}\n\n" +
                    $"## Issue\n{issue.Trim()}\n\n" +
                    $"## Evidence\n{evidence.Trim()}\n\n" +
                    $"## Task\n{task.Trim()}\n";

                File.WriteAllText(path, content, Encoding.UTF8);
            }
            catch (UnauthorizedAccessException)
            {
                return $"Error: Access denied — cannot write bug report entry for title '{title}'.";
            }
            catch (Exception ex)
            {
                return $"Error: Failed to write bug report entry: {ex.Message}";
            }

            return "✔ Bug report written.\n" +
                   $"  Title    : {title.Trim()}\n" +
                   $"  Tool     : {toolName.Trim()}\n" +
                   $"  Date     : {now:yyyy-MM-dd HH:mm:ss}\n" +
                   $"  File     : {path}\n";
        }
    }
}
