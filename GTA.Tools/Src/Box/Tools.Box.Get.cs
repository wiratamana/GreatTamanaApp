using System.Text.Json.Nodes;

namespace GTA.Tools
{
    internal static partial class ToolsBoxApi
    {
        [ToolTag("box_api")]
        [ToolInfo(
            name: "box_get",
            description:
                "Send a raw HTTP GET request to any Box API URL over the pure-HTTP ADFS/SAML " +
                "Box session. Session cookies and the CSRF " +
                "token (X-Request-Token) are injected automatically. " +
                "Use this to explore endpoints, debug, or call any Box API path not covered by " +
                "the other tools. " +
                "Examples:\n" +
                "  • https://your-company.ent.box.com/app-api/enduserapp/folder/0\n" +
                "  • https://your-company.ent.box.com/app-api/enduserapp/current-user\n" +
                "  • https://your-company.ent.box.com/api/2.0/files/123456 (needs bearer_token)")]
        [Parameter(index: 0, name: "url",
            type: "string",
            description: "Full URL to call. Must start with https://your-company.ent.box.com/",
            required: true)]
        [Parameter(index: 1, name: "bearer_token",
            type: "string",
            description: "Optional OAuth Bearer token. Replaces session CSRF auth with " +
                         "'Authorization: Bearer <token>' header.",
            required: false)]
        [Parameter(index: 2, name: "timeout_ms",
            type: "integer",
            description: "Request timeout in milliseconds. Default: 30000.",
            required: false)]
        public static object BoxRawGet(JsonObject input)
        {
            string? url = input["url"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(url))
                return "Error: 'url' is required.";

            string? bearer  = input["bearer_token"]?.GetValue<string>();
            int     timeout = input["timeout_ms"]?.GetValue<int>() ?? 30_000;

            var result = BoxGet(url, bearer: bearer, timeoutMs: timeout);
            return FormatResponse($"GET {url}", result);
        }
    }
}
