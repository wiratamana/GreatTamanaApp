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
            name: "ibm_cloud_logs_post",
            description:
                "Send a raw HTTP POST request (with a raw JSON body you build yourself) to any IBM " +
                "Cloud Logs API URL via a direct HttpClient call. " +
                "Auth is 100% cookie-based — a 'coralogix_token' is resolved automatically (environment " +
                "variable override, then a fully automated login+refresh pipeline scoped to the selected " +
                "environment), sent as a Cookie header alongside the required 'cgx-team-id' and 'Origin' " +
                "headers; 'Content-Type: application/json' is set automatically. Works out of the box. " +
                "Prefer ibm_cloud_logs_search / ibm_cloud_logs_stats for normal log searches — use this " +
                "only for endpoints/payload shapes those tools don't cover. IMPORTANT: the 'url' host must " +
                "actually match the tenant of whichever 'environment' you select, otherwise the auth " +
                "cookie/team-id sent won't match the host being called and IBM Cloud Logs will reject the " +
                "request. ONLY SUPPORTS EXACTLY 4 ENVIRONMENTS (see the 'environment' parameter) — tenant/" +
                "region/team-id are configured per environment and cannot be overridden.")]
        [Parameter(index: 0, name: "url",
            type: "string",
            description: "Full URL to POST to. Must be a *.api.<region>.logs.cloud.ibm.com host that " +
                         "matches the tenant of the selected 'environment'. Example (jp-prod host): " +
                         "https://<tenant-guid>.api.jp-tok.logs.cloud.ibm.com/api/v1/logquery",
            required: true)]
        [Parameter(index: 1, name: "body",
            type: "string",
            description: "Raw JSON string request body to send.",
            required: true)]
        [Parameter(index: 2, name: "environment",
            type: "string",
            description: "Which IBM Cloud Logs environment this call targets (used to build the 'Origin'/" +
                         "'cgx-team-id' headers and to resolve the right auth token). ONLY SUPPORTS EXACTLY " +
                         "4 VALUES: 'jp-prod', 'jp-preprod', 'eu-prod', 'eu-preprod'. Default: 'jp-prod'.",
            required: false)]
        [Parameter(index: 3, name: "timeout_ms",
            type: "integer",
            description: "Request timeout in milliseconds. Default: 30000.",
            required: false)]
        public static object IbmCloudRawPost(JsonObject input)
        {
            string? url = TryGetString(input, "url");
            if (string.IsNullOrWhiteSpace(url))
                return "Error: 'url' is required.";

            string body    = TryGetString(input, "body") ?? "{}";
            int    timeout = GetIntOrDefault(input, "timeout_ms", 30_000);

            IbmCloudEnvironmentInfo env;
            try { env = ResolveEnvironmentParam(input); }
            catch (Exception ex) { return $"Error: {ex.Message}"; }

            return IbmCloudLogsApiClient.RawPost(url, body, env, timeout);
        }
    }
}
