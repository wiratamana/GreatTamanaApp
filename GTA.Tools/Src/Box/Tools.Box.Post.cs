using System.Text.Json.Nodes;

namespace GTA.Tools
{
    internal static partial class ToolsBoxApi
    {
        [ToolTag("box_api")]
        [ToolInfo(
            name: "box_post",
            description:
                "Send a raw HTTP POST request to any Box API URL over the pure-HTTP ADFS/SAML " +
                "Box session. " +
                "Session cookies and the CSRF token are injected automatically. " +
                "Content-Type: application/json is set automatically. " +
                "Use this to call POST endpoints not covered by the other tools, " +
                "or to test Box API endpoints directly. " +
                "Examples:\n" +
                "  • POST https://your-company.ent.box.com/api/2.0/folders  (needs bearer_token)\n" +
                "  • POST https://your-company.ent.box.com/ai/ask           (needs bearer_token)")]
        [Parameter(index: 0, name: "url",
            type: "string",
            description: "Full URL to POST to. Must start with https://your-company.ent.box.com/",
            required: true)]
        [Parameter(index: 1, name: "body",
            type: "string",
            description: "JSON string to send as the request body. " +
                         "Example: '{\"name\":\"New Folder\",\"parent\":{\"id\":\"0\"}}'",
            required: false)]
        [Parameter(index: 2, name: "bearer_token",
            type: "string",
            description: "Optional OAuth Bearer token. Replaces session CSRF auth with " +
                         "'Authorization: Bearer <token>' header.",
            required: false)]
        [Parameter(index: 3, name: "timeout_ms",
            type: "integer",
            description: "Request timeout in milliseconds. Default: 30000.",
            required: false)]
        public static object BoxRawPost(JsonObject input)
        {
            string? url = input["url"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(url))
                return "Error: 'url' is required.";

            string? body    = input["body"]?.GetValue<string>() ?? "{}";
            string? bearer  = input["bearer_token"]?.GetValue<string>();
            int     timeout = input["timeout_ms"]?.GetValue<int>() ?? 30_000;

            var result = BoxPost(url, body: body, bearer: bearer, timeoutMs: timeout);
            return FormatResponse($"POST {url}", result);
        }
    }
}
