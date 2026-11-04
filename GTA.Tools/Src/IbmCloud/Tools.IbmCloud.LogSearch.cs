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
            name: "ibm_cloud_logs_search",
            description:
                "Search IBM Cloud Logs by keyword and/or timestamp range using the internal dashboard " +
                "REST API (POST /api/v1/logquery), via a direct HttpClient call. " +
                "Auth is 100% cookie-based (a 'coralogix_token' cookie, " +
                "resolved automatically with a self-refreshing automated login pipeline — works out of the box). " +
                "Provide 'query' to search for an exact keyword/phrase (e.g. an error code like " +
                "'MSMA61170E'), and/or a time range via start_time_ms/end_time_ms, or the simpler " +
                "minutes_back (e.g. 15 = last 15 minutes). Returns each matching log entry's timestamp, " +
                "severity, application/subsystem name, and the COMPLETE raw log line text — nothing is " +
                "truncated, every matched entry up to 'page_size' is included in full. " +
                "TIP: do NOT filter by application/subsystem — the real root cause of an error is often " +
                "logged by a completely different service than the one that surfaced the user-facing error. " +
                "ONLY SUPPORTS EXACTLY 4 ENVIRONMENTS (see the 'environment' parameter) — tenant/region/" +
                "team-id are configured per environment and cannot be overridden.")]
        [Parameter(index: 0, name: "query",
            type: "string",
            description: "Keyword or exact phrase to search for, e.g. 'MSMA61170E'. Leave empty (or omit) " +
                         "to just browse all logs in the given time range — the tool automatically " +
                         "switches to a wildcard '*' search in that case.",
            required: false)]
        [Parameter(index: 1, name: "start_time_ms",
            type: "integer",
            description: "Explicit start of the time range, as Unix epoch milliseconds. Overrides " +
                         "minutes_back when provided together with end_time_ms.",
            required: false)]
        [Parameter(index: 2, name: "end_time_ms",
            type: "integer",
            description: "Explicit end of the time range, as Unix epoch milliseconds. Default: now.",
            required: false)]
        [Parameter(index: 3, name: "minutes_back",
            type: "integer",
            description: "Convenience time-range shortcut: search the last N minutes ending now. " +
                         "Ignored if start_time_ms/end_time_ms are provided. Default: 15.",
            required: false)]
        [Parameter(index: 4, name: "query_type",
            type: "string",
            description: "Query match type: 'exact' (default, matches the full keyword/phrase) or " +
                         "'wildcard' (for patterns using '*'/'?').",
            required: false)]
        [Parameter(index: 5, name: "syntax",
            type: "string",
            description: "Query syntax: 'Lucene' (default) or 'DataPrime'.",
            required: false)]
        [Parameter(index: 6, name: "page_size",
            type: "integer",
            description: "Maximum number of log entries to return. Default: 100.",
            required: false)]
        [Parameter(index: 7, name: "page_index",
            type: "integer",
            description: "0-based page index for pagination. Default: 0.",
            required: false)]
        [Parameter(index: 8, name: "environment",
            type: "string",
            description: "Which IBM Cloud Logs environment to query. ONLY SUPPORTS EXACTLY 4 VALUES: " +
                         "'jp-prod', 'jp-preprod', 'eu-prod', 'eu-preprod'. Each one's tenant GUID, IBM " +
                         "Cloud region, and Coralogix team-id are configured in application settings (no other value is " +
                         "accepted) — this also selects which cached/auto-refreshed Coralogix auth token " +
                         "is used, fully automatically. Default: 'jp-prod'.",
            required: false)]
        [Parameter(index: 9, name: "timeout_ms",
            type: "integer",
            description: "Request timeout in milliseconds. Default: 30000.",
            required: false)]
        public static object IbmCloudLogsSearch(JsonObject input)
        {
            string  query      = TryGetString(input, "query") ?? "";
            long?   startMsIn  = TryGetLong(input, "start_time_ms");
            long?   endMsIn    = TryGetLong(input, "end_time_ms");
            int     minutesBack= GetIntOrDefault(input, "minutes_back", 15);
            string  queryType  = TryGetString(input, "query_type") ?? "exact";
            string  syntax     = TryGetString(input, "syntax") ?? "Lucene";
            int     pageSize   = GetIntOrDefault(input, "page_size", 100);
            int     pageIndex  = GetIntOrDefault(input, "page_index", 0);
            int     timeout    = GetIntOrDefault(input, "timeout_ms", 30_000);

            IbmCloudEnvironmentInfo env;
            try { env = ResolveEnvironmentParam(input); }
            catch (Exception ex) { return $"Error: {ex.Message}"; }

            return IbmCloudLogsApiClient.SearchLogs(
                query, startMsIn, endMsIn, minutesBack, queryType, syntax, pageSize, pageIndex, env, timeout);
        }
    }
}
