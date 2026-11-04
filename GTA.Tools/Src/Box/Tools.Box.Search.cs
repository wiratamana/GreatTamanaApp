using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GTA.Tools
{
    internal static partial class ToolsBoxApi
    {
        [ToolTag("box_api")]
        [ToolInfo(
            name: "box_search",
            description:
                "Search Box for files and folders by keyword using the internal Box web API. " +
                "Calls GET /app-api/enduserapp/folder/{folder_id}/search?query={query}. " +
                "Uses session cookies + CSRF token automatically — no Bearer token required. " +
                "Use folder_id '0' (default) to search globally across all accessible files. " +
                "Returns matched items with id, name, type, extension, path, lastUpdatedByName, " +
                "and fileContentExcerpt (content snippet with highlighted matches). " +
                "Also returns resultCount and pageCount for pagination.")]
        [Parameter(index: 0, name: "query",
            type: "string",
            description: "Search keyword or phrase. Example: 'RoutingServer', 'Design Spec'.",
            required: true)]
        [Parameter(index: 1, name: "folder_id",
            type: "string",
            description: "Folder ID to search within. Use '0' for global search across all files. Default: '0'.",
            required: false)]
        [Parameter(index: 2, name: "limit",
            type: "integer",
            description: "Maximum number of results per page. Default: 20.",
            required: false)]
        [Parameter(index: 3, name: "page",
            type: "integer",
            description: "Page number (1-based) for pagination. Default: 1.",
            required: false)]
        [Parameter(index: 4, name: "timeout_ms",
            type: "integer",
            description: "Request timeout in milliseconds. Default: 30000.",
            required: false)]
        public static object BoxSearch(JsonObject input)
        {
            string? query = input["query"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(query))
                return "Error: 'query' is required.";

            string folderId = input["folder_id"]?.GetValue<string>() ?? "0";
            int    limit    = input["limit"]?.GetValue<int>() ?? 20;
            int    page     = input["page"]?.GetValue<int>() ?? 1;
            int    timeout  = input["timeout_ms"]?.GetValue<int>() ?? 30_000;

            string url = $"{InternalBase}/folder/{folderId}/search" +
                         $"?query={Uri.EscapeDataString(query)}" +
                         $"&limit={limit}" +
                         $"&page={page}";

            var result = BoxGet(url, timeoutMs: timeout);
            return FormatBoxSearchResponse(result, query, page);
        }

        private static string FormatBoxSearchResponse(BoxResult result, string query, int page)
        {
            if (!result.Ok)
                return FormatError($"box_search (query=\"{query}\")", result);

            try
            {
                using var doc  = JsonDocument.Parse(result.Body!);
                var       root = doc.RootElement;

                int resultCount = root.TryGetProperty("resultCount", out var rcEl) ? rcEl.GetInt32() : 0;
                int pageNumber  = root.TryGetProperty("pageNumber",  out var pnEl) ? pnEl.GetInt32() : page;
                int pageCount   = root.TryGetProperty("pageCount",   out var pcEl) ? pcEl.GetInt32() : 1;
                int pageSize    = root.TryGetProperty("pageSize",    out var psEl) ? psEl.GetInt32() : 20;

                // Build typedID → excerpt map from searchItems
                var excerptMap = new Dictionary<string, string>(StringComparer.Ordinal);
                if (root.TryGetProperty("searchItems", out var searchItems) &&
                    searchItems.ValueKind == JsonValueKind.Array)
                {
                    foreach (var si in searchItems.EnumerateArray())
                    {
                        string tid = si.TryGetProperty("typedID",           out var tEl) ? (tEl.GetString()  ?? "") : "";
                        string exc = si.TryGetProperty("fileContentExcerpt",out var eEl) ? (eEl.GetString()  ?? "") : "";
                        if (!string.IsNullOrEmpty(tid) && !string.IsNullOrEmpty(exc))
                            excerptMap[tid] = exc;
                    }
                }

                var sb = new StringBuilder();
                sb.AppendLine("╔══════════════════════════════════════════════════╗");
                sb.AppendLine($"  Box Search : \"{query}\"");
                sb.AppendLine($"  Results    : {resultCount:N0} total | Page {pageNumber} of {pageCount} (up to {pageSize} shown)");
                sb.AppendLine("╚══════════════════════════════════════════════════╝");
                sb.AppendLine();

                if (!root.TryGetProperty("items", out var items) ||
                    items.ValueKind != JsonValueKind.Array ||
                    items.GetArrayLength() == 0)
                {
                    sb.AppendLine("(no items returned)");
                    return sb.ToString().TrimEnd();
                }

                int n = 1;
                foreach (var item in items.EnumerateArray())
                {
                    string typedId   = item.TryGetProperty("typedID",           out var tidEl)  ? (tidEl.GetString()  ?? "") : "";
                    string name      = item.TryGetProperty("name",              out var nmEl)   ? (nmEl.GetString()   ?? "") : "";
                    string type      = item.TryGetProperty("type",              out var tpEl)   ? (tpEl.GetString()   ?? "") : "";
                    string ext       = item.TryGetProperty("extension",         out var exEl)   ? (exEl.GetString()   ?? "") : "";
                    string id        = item.TryGetProperty("id",                out var idEl)   ? (idEl.GetString()   ?? "") : "";
                    string updatedBy = item.TryGetProperty("lastUpdatedByName", out var ubEl)   ? (ubEl.GetString()   ?? "") : "";

                    // Date — stored as a Unix-timestamp string in the Box API
                    long dateUnix = 0;
                    if (item.TryGetProperty("date", out var dateEl))
                    {
                        if      (dateEl.ValueKind == JsonValueKind.Number) dateUnix = dateEl.GetInt64();
                        else if (dateEl.ValueKind == JsonValueKind.String) long.TryParse(dateEl.GetString(), out dateUnix);
                    }

                    // Breadcrumb path (skip root folder id="0")
                    var pathParts = new List<string>();
                    if (item.TryGetProperty("path", out var pathEl) &&
                        pathEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var seg in pathEl.EnumerateArray())
                        {
                                string segId   = seg.TryGetProperty("id",   out var siEl) ? siEl.ToString() : "";
                            string segName = seg.TryGetProperty("name", out var snEl) ? (snEl.GetString() ?? "") : "";
                            if (segId == "0" || string.IsNullOrWhiteSpace(segName)) continue;
                            pathParts.Add(segName);
                        }
                    }

                    string icon    = type == "folder" ? "📁" : "📄";
                    string typeStr = string.IsNullOrEmpty(ext) ? type : $"{type} ({ext})";
                    string dateStr = dateUnix > 0
                        ? DateTimeOffset.FromUnixTimeSeconds(dateUnix).ToString("yyyy-MM-dd")
                        : "";

                    sb.AppendLine($"┌─[{n}] {icon} {name}");
                    sb.AppendLine($"│   ID      : {id}");
                    sb.AppendLine($"│   Type    : {typeStr}");
                    if (!string.IsNullOrEmpty(dateStr))
                        sb.AppendLine($"│   Updated : {dateStr}  by  {updatedBy}");
                    if (pathParts.Count > 0)
                        sb.AppendLine($"│   Path    : {string.Join(" › ", pathParts)}");

                    // Excerpt — strip HTML <span> tags, collapse whitespace, cap at 160 chars
                    if (excerptMap.TryGetValue(typedId, out string? rawExcerpt) &&
                        !string.IsNullOrWhiteSpace(rawExcerpt))
                    {
                        string cleaned = Regex.Replace(rawExcerpt, @"<[^>]+>", "");
                        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
                        if (cleaned.Length > 160) cleaned = cleaned.Substring(0, 157) + "...";
                        sb.AppendLine($"│   Excerpt : {cleaned}");
                    }

                    sb.AppendLine("└────────────────────────────────");
                    n++;
                }

                if (pageNumber < pageCount)
                {
                    sb.AppendLine();
                    sb.AppendLine($"💡 More results available — use page={pageNumber + 1} to continue " +
                                  $"({resultCount - (pageNumber * pageSize)} remaining).");
                }

                return sb.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                // Graceful fallback: show parse error + raw body snippet
                string snippet = result.Body?.Length > 300
                    ? result.Body.Substring(0, 300) + "..."
                    : result.Body ?? "";
                return $"⚠️ box_search — response parse failed: {ex.Message}\n\nRaw (first 300 chars):\n{snippet}";
            }
        }
    }
}
