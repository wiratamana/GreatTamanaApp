using System;
using System.Text.Json.Nodes;
using GTA.Tools.Impl.IbmCloud;
using IbmCloudEnvironmentInfo = GTA.Tools.Impl.IbmCloud.IbmCloudLogsApiClient.IbmCloudEnvironmentInfo;

namespace GTA.Tools
{
    internal static partial class ToolsIbmCloudApi
    {
        [ToolTag("ibm_cloud_api")]
        [ToolInfo(
            name: "ibm_cloud_logs_investigate_trace",
            description:
                "Automated root-cause investigation tool for IBM Cloud Logs. Given an error code/keyword " +
                "(e.g. 'MSMA61170E') and an optional approximate event timestamp: (1) finds matching log " +
                "entries for the keyword, (2) extracts the executor THREAD NAME embedded in the matched " +
                "log line (e.g. '[Default Executor-thread-1]') — the correct per-request correlation " +
                "key (NOT jsonUuid, which is a log-shipper batch id that matches unrelated requests), " +
                "(3) re-queries by that exact thread name in a narrow time window with ascending sort to " +
                "reconstruct the full request lifecycle across every application/subsystem involved " +
                "(deliberately not filtered — the real root cause is often logged by a different service " +
                "than the one that surfaced the error), and (4) runs a company-wide frequency check for " +
                "the same keyword across a much wider window to report how rare/common the issue is. Every " +
                "matched log line at every step is returned in full — nothing is truncated. " +
                "Collapses what would otherwise be ~6 manual ibm_cloud_logs_search round-trips into one call. " +
                "ONLY SUPPORTS EXACTLY 4 ENVIRONMENTS (see the 'environment' parameter) — tenant/region/" +
                "team-id are configured per environment and cannot be overridden.")]
        [Parameter(index: 0, name: "keyword",
            type: "string",
            description: "The error code or keyword to investigate, e.g. 'MSMA61170E'.",
            required: true)]
        [Parameter(index: 1, name: "event_time_ms",
            type: "integer",
            description: "Approximate Unix epoch ms timestamp of the event. If omitted, the timestamp of " +
                         "the newest matching log entry found in the initial search is used as the anchor.",
            required: false)]
        [Parameter(index: 2, name: "initial_window_minutes",
            type: "integer",
            description: "Minutes before/after event_time_ms (or before 'now' if event_time_ms is omitted) " +
                         "to search for the initial keyword match. Default: 15.",
            required: false)]
        [Parameter(index: 3, name: "thread_window_seconds_before",
            type: "integer",
            description: "Seconds before the anchor timestamp to include in the narrow thread-trace " +
                         "reconstruction window. Default: 20.",
            required: false)]
        [Parameter(index: 4, name: "thread_window_seconds_after",
            type: "integer",
            description: "Seconds after the anchor timestamp to include in the narrow thread-trace " +
                         "reconstruction window. Default: 30.",
            required: false)]
        [Parameter(index: 5, name: "frequency_window_hours",
            type: "integer",
            description: "Hours before/after the anchor timestamp to search company-wide for the same " +
                         "keyword, to report how rare/common the issue is. Default: 6.",
            required: false)]
        [Parameter(index: 6, name: "page_size",
            type: "integer",
            description: "Maximum log entries to return per query stage. Default: 200.",
            required: false)]
        [Parameter(index: 7, name: "environment",
            type: "string",
            description: "Which IBM Cloud Logs environment to investigate. ONLY SUPPORTS EXACTLY 4 VALUES: " +
                         "'jp-prod', 'jp-preprod', 'eu-prod', 'eu-preprod'. Each one's tenant GUID, IBM " +
                         "Cloud region, and Coralogix team-id are configured in application settings (no other value is " +
                         "accepted) — this also selects which cached/auto-refreshed Coralogix auth token " +
                         "is used, fully automatically. Default: 'jp-prod'.",
            required: false)]
        [Parameter(index: 8, name: "timeout_ms",
            type: "integer",
            description: "Request timeout in milliseconds (per query stage). Default: 30000.",
            required: false)]
        public static object IbmCloudLogsInvestigateTrace(JsonObject input)
        {
            string? keyword = TryGetString(input, "keyword");
            if (string.IsNullOrWhiteSpace(keyword))
                return "Error: 'keyword' is required.";

            long?  eventTimeMsIn         = TryGetLong(input, "event_time_ms");
            int    initialWindowMinutes  = GetIntOrDefault(input, "initial_window_minutes", 15);
            int    threadBeforeSeconds   = GetIntOrDefault(input, "thread_window_seconds_before", 20);
            int    threadAfterSeconds    = GetIntOrDefault(input, "thread_window_seconds_after", 30);
            int    frequencyWindowHours  = GetIntOrDefault(input, "frequency_window_hours", 6);
            int    pageSize              = GetIntOrDefault(input, "page_size", 200);
            int    timeout               = GetIntOrDefault(input, "timeout_ms", 30_000);

            IbmCloudEnvironmentInfo env;
            try { env = ResolveEnvironmentParam(input); }
            catch (Exception ex) { return $"Error: {ex.Message}"; }

            return IbmCloudLogsApiClient.InvestigateTrace(
                keyword, eventTimeMsIn, initialWindowMinutes, threadBeforeSeconds, threadAfterSeconds,
                frequencyWindowHours, pageSize, env, timeout);
        }
    }
}
