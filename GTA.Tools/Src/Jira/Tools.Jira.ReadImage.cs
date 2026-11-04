using System.Text.Json.Nodes;
using GTA.Tools.Impl.Common;

namespace GTA.Tools
{
    internal static partial class ToolsJiraApi
    {
        [ToolTag("jira_api")]
        [ToolInfo(
            name: "jira_read_image",
            description:
                "Download a JIRA attachment image using the JIRA REST API (HTTP Basic Auth " +
                "with an API token) and feed it directly " +
                "to the AI as a base64 image content block so " +
                "the AI can see and understand the image. " +
                "Provide either the full attachment 'url' (from jira_read_ticket attachment " +
                "list) or just the numeric 'attachment_id'. " +
                "Automatically retries (up to 10×) if the download fails or returns empty/" +
                "malformed image data — a common symptom of flaky proxy/internet connections. " +
                "Returns the same image content block format as load_image. " +
                "Supported formats: PNG, JPG/JPEG, GIF, WEBP.")]
        [Parameter(index: 0, name: "url",
            type: "string",
            description: "Full JIRA attachment content URL. " +
                         "Example: https://your-company.atlassian.net/rest/api/3/attachment/content/12345. " +
                         "Obtain this from the 'URL' field in jira_read_ticket attachment list. " +
                         "Either 'url' or 'attachment_id' is required.",
            required: false)]
        [Parameter(index: 1, name: "attachment_id",
            type: "string",
            description: "JIRA attachment numeric ID (shown in brackets in jira_read_ticket output). " +
                         "Example: '12345'. The URL is constructed as " +
                         "{jira_base_url}/rest/api/3/attachment/content/{attachment_id}. " +
                         "Either 'url' or 'attachment_id' is required.",
            required: false)]
        [Parameter(index: 2, name: "jira_base_url",
            type: "string",
            description: "JIRA instance base URL. Only used when 'attachment_id' is provided. " +
                         "Default: the Jira base URL configured in init.ini.",
            required: false)]
        [Parameter(index: 3, name: "timeout_ms",
            type: "integer",
            description: "Download timeout in milliseconds. Default: 30000 (30 seconds).",
            required: false)]
        public static object JiraReadImage(JsonObject input)
        {
            string? url = ResolveJiraAttachmentUrl(input);
            if (string.IsNullOrWhiteSpace(url))
                return "Error: Provide either 'url' (full attachment URL) or 'attachment_id'.";

            int timeoutMs = input["timeout_ms"]?.GetValue<int>() ?? 30_000;

            var (success, result, lastError) = ToolsCommonPipe.RetryUntilValid(
                attempt: () => SendCommandForImage(new { command = "DownloadImageAsBase64", url, timeoutMs }),
                isValid: JiraIsValidImageResult,
                describeInvalid: r => r as string ??
                    "Image data returned was empty or malformed (possible corrupted/truncated download).");

            if (!success)
                return $"❌ jira_read_image — Download failed after {ToolsCommonPipe.MaxDownloadRetries} attempt(s)\n" +
                       $"Error: {lastError}";

            return result!;
        }

        private static bool JiraIsValidImageResult(object result)
        {
            if (result is JsonArray arr && arr.Count > 0 &&
                arr[0] is JsonObject block &&
                block["source"] is JsonObject source &&
                source["data"] is JsonNode dataNode)
            {
                string? data = dataNode.GetValue<string>();
                return !string.IsNullOrEmpty(data);
            }

            return false;
        }
    }
}
