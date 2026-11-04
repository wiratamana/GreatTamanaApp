using System.Text.Json;
using System.Text.Json.Nodes;

namespace GTA.Tools
{
    internal static partial class ToolsBoxApi
    {
        [ToolTag("box_api")]
        [ToolInfo(
            name: "box_api_sanity_check",
            description:
                "Verify that the Box session is active and the internal API is reachable. " +
                "Calls GET /app-api/enduserapp/current-user over a pure-HTTP ADFS/SAML " +
                "Box session — cookies + the CSRF token " +
                "(auto-read from the session's own cookie jar). " +
                "Returns the logged-in user's name and email when successful. " +
                "Use this as a health-check before making other Box API calls.")]
        public static object BoxApiSanityCheck(JsonObject input)
        {
            var result = BoxGet($"{InternalBase}/current-user");

            if (!result.Ok)
                return FormatError("GET /app-api/enduserapp/current-user", result);

            string userLine = ParseCurrentUser(result.Body);
            return
                $"✅ Box API OK — HTTP {result.Status}\n" +
                (!string.IsNullOrEmpty(userLine) ? userLine + "\n" : "") +
                $"--- Response ---\n{result.Body}".TrimEnd();
        }

        private static string ParseCurrentUser(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return "";
            try
            {
                using var doc  = JsonDocument.Parse(json);
                var       root = doc.RootElement;
                string? name  = root.TryGetProperty("name",  out var n) ? n.GetString() : null;
                string? email = root.TryGetProperty("email", out var e) ? e.GetString() : null;
                return (name != null || email != null)
                    ? $"Logged in as: {name ?? "?"} <{email ?? "?"}>"
                    : "";
            }
            catch { return ""; }
        }
    }
}
