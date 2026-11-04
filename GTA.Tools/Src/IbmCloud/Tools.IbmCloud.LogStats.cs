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
            name: "ibm_cloud_logs_stats",
            description:
                "Get just the MATCH COUNT (not the full log bodies) for an IBM Cloud Logs keyword/time-" +
                "range search using the internal dashboard REST API (POST /api/v1/logquery/stats). Same " +
                "auth and parameters as ibm_cloud_logs_search, but far cheaper — use this first to check " +
                "whether a keyword has any hits in a time range before calling ibm_cloud_logs_search for " +
                "the full entries. Returns logsCount (number of matching log lines) and templateCount. " +
                "ONLY SUPPORTS EXACTLY 4 ENVIRONMENTS (see the 'environment' parameter) — tenant/region/" +
                "team-id are configured per environment and cannot be overridden.")]
        [Parameter(index: 0, name: "query",
            type: "string",
            description: "Keyword or exact phrase to search for, e.g. 'MSMA61170E'. Leave empty to count " +
                         "all logs in the given time range (wildcard '*').",
            required: false)]
        [Parameter(index: 1, name: "start_time_ms",
            type: "integer",
            description: "Explicit start of the time range, as Unix epoch milliseconds.",
            required: false)]
        [Parameter(index: 2, name: "end_time_ms",
            type: "integer",
            description: "Explicit end of the time range, as Unix epoch milliseconds. Default: now.",
            required: false)]
        [Parameter(index: 3, name: "minutes_back",
            type: "integer",
            description: "Convenience time-range shortcut: count over the last N minutes ending now. " +
                         "Ignored if start_time_ms/end_time_ms are provided. Default: 15.",
            required: false)]
        [Parameter(index: 4, name: "query_type",
            type: "string",
            description: "Query match type: 'exact' (default) or 'wildcard'.",
            required: false)]
        [Parameter(index: 5, name: "syntax",
            type: "string",
            description: "Query syntax: 'Lucene' (default) or 'DataPrime'.",
            required: false)]
        [Parameter(index: 6, name: "environment",
            type: "string",
            description: "Which IBM Cloud Logs environment to query. ONLY SUPPORTS EXACTLY 4 VALUES: " +
                         "'jp-prod', 'jp-preprod', 'eu-prod', 'eu-preprod'. Each one's tenant GUID, IBM " +
                         "Cloud region, and Coralogix team-id are configured in application settings (no other value is " +
                         "accepted) — this also selects which cached/auto-refreshed Coralogix auth token " +
                         "is used, fully automatically. Default: 'jp-prod'.",
            required: false)]
        [Parameter(index: 7, name: "timeout_ms",
            type: "integer",
            description: "Request timeout in milliseconds. Default: 30000.",
            required: false)]
        public static object IbmCloudLogsStats(JsonObject input)
        {
            string  query       = TryGetString(input, "query") ?? "";
            long?   startMsIn   = TryGetLong(input, "start_time_ms");
            long?   endMsIn     = TryGetLong(input, "end_time_ms");
            int     minutesBack = GetIntOrDefault(input, "minutes_back", 15);
            string  queryType   = TryGetString(input, "query_type") ?? "exact";
            string  syntax      = TryGetString(input, "syntax") ?? "Lucene";
            int     timeout     = GetIntOrDefault(input, "timeout_ms", 30_000);

            IbmCloudEnvironmentInfo env;
            try { env = ResolveEnvironmentParam(input); }
            catch (Exception ex) { return $"Error: {ex.Message}"; }

            return IbmCloudLogsApiClient.GetStats(
                query, startMsIn, endMsIn, minutesBack, queryType, syntax, env, timeout);
        }
    }
}
