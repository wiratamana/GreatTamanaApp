using System;
using System.Text.Json.Nodes;
using GTA.Tools.Impl.IbmCloud;
using IbmCloudEnvironmentInfo = GTA.Tools.Impl.IbmCloud.IbmCloudLogsApiClient.IbmCloudEnvironmentInfo;

namespace GTA.Tools
{
    internal static partial class ToolsIbmCloudApi
    {
        [ToolTag("ibm_cloud_api")]
        [FileMutating]
        [ToolInfo(
            name: "ibm_cloud_logs_export_evidence",
            description:
                "Run a batch of IBM Cloud Logs investigative queries and write the COMPLETE, pretty-" +
                "printed JSON response for each to disk as evidence files, plus a manifest describing " +
                "every query used. By default (redact=true) sensitive values (JWT tokens, cookies, IP " +
                "addresses, Envoy/Istio peer-metadata) are replaced with clear '[REDACTED_xxx]' markers " +
                "while every diagnostically-relevant field (entity IDs, timestamps, error codes/messages, " +
                "endpoint paths, thread IDs) is preserved exactly. Use this to produce a shareable evidence " +
                "package for a root-cause investigation hand-off. " +
                "ONLY SUPPORTS EXACTLY 4 ENVIRONMENTS (see the 'environment' parameter) — tenant/region/" +
                "team-id are configured per environment and cannot be overridden.")]
        [Parameter(index: 0, name: "queries",
            type: "string",
            description: "JSON array of query definitions to run, each: " +
                         "{\"label\":\"...\",\"keyword\":\"...\",\"start_ms\":123,\"end_ms\":456}. " +
                         "'label' is used to name the output file (sanitized). Example: " +
                         "[{\"label\":\"thread_trace\",\"keyword\":\"Default Executor-thread-1\"," +
                         "\"start_ms\":1700000000000,\"end_ms\":1700000025000}]",
            required: true)]
        [Parameter(index: 1, name: "output_dir",
            type: "string",
            description: "Full absolute Windows path of the directory to write evidence files into. " +
                         "Created automatically if it doesn't exist.",
            required: true)]
        [Parameter(index: 2, name: "redact",
            type: "boolean",
            description: "If true (default), redact JWTs/cookies/IPs/Envoy-metadata with '[REDACTED_xxx]' " +
                         "markers. If false, write the exact unredacted response — handle with care.",
            required: false)]
        [Parameter(index: 3, name: "page_size",
            type: "integer",
            description: "Maximum log entries to fetch per query. Default: 500.",
            required: false)]
        [Parameter(index: 4, name: "environment",
            type: "string",
            description: "Which IBM Cloud Logs environment to export from. ONLY SUPPORTS EXACTLY 4 VALUES: " +
                         "'jp-prod', 'jp-preprod', 'eu-prod', 'eu-preprod'. Each one's tenant GUID, IBM " +
                         "Cloud region, and Coralogix team-id are configured in application settings (no other value is " +
                         "accepted) — this also selects which cached/auto-refreshed Coralogix auth token " +
                         "is used, fully automatically. Default: 'jp-prod'.",
            required: false)]
        [Parameter(index: 5, name: "timeout_ms",
            type: "integer",
            description: "Request timeout in milliseconds per query. Default: 30000.",
            required: false)]
        public static object IbmCloudLogsExportEvidence(JsonObject input)
        {
            string? queriesJson = TryGetString(input, "queries");
            string? outputDir   = TryGetString(input, "output_dir");

            if (string.IsNullOrWhiteSpace(queriesJson))
                return "Error: 'queries' is required (JSON array of {label, keyword, start_ms, end_ms}).";
            if (string.IsNullOrWhiteSpace(outputDir))
                return "Error: 'output_dir' is required.";

            bool redact   = GetBoolOrDefault(input, "redact", true);
            int  pageSize = GetIntOrDefault(input, "page_size", 500);
            int  timeout  = GetIntOrDefault(input, "timeout_ms", 30_000);

            IbmCloudEnvironmentInfo env;
            try { env = ResolveEnvironmentParam(input); }
            catch (Exception ex) { return $"Error: {ex.Message}"; }

            return IbmCloudLogsApiClient.ExportEvidence(queriesJson, outputDir, redact, pageSize, env, timeout);
        }
    }
}
