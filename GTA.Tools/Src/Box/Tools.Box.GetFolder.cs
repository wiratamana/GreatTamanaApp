using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GTA.Tools
{
    internal static partial class ToolsBoxApi
    {
        [ToolTag("box_api")]
        [ToolInfo(
            name: "box_get_folder",
            description:
                "Get the full contents of a Box folder using the internal Box web API. " +
                "Returns a human-readable summary with folder name, breadcrumb path, owner, " +
                "total item count, and a list of all files and subfolders. " +
                "Each item shows: icon (📁/📄), name, ID, type/extension, last updated date and who updated it. " +
                "Uses session cookies + CSRF token automatically — no Bearer token required. " +
                "Use folder_id '0' for the root 'All Files' folder.")]
        [Parameter(index: 0, name: "folder_id",
            type: "string",
            description: "Numeric Box folder ID. Use '0' for the root folder. Example: '123456789012'.",
            required: true)]
        [Parameter(index: 1, name: "timeout_ms",
            type: "integer",
            description: "Request timeout in milliseconds. Default: 30000.",
            required: false)]
        public static object BoxGetFolder(JsonObject input)
        {
            string? folderId = input["folder_id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(folderId))
                return "Error: 'folder_id' is required. Use '0' for the root folder.";

            int timeout = input["timeout_ms"]?.GetValue<int>() ?? 30_000;

            var result = BoxGet($"{InternalBase}/folder/{folderId}", timeoutMs: timeout);
            return FormatBoxFolderResponse(result, folderId);
        }

        private static string FormatBoxFolderResponse(BoxResult result, string folderId)
        {
            if (!result.Ok)
                return FormatError($"box_get_folder (folder_id=\"{folderId}\")", result);

            try
            {
                using var doc  = JsonDocument.Parse(result.Body!);
                var       root = doc.RootElement;

                // Folder metadata
                string folderName    = "";
                string ownerName     = "";
                var    breadcrumb    = new List<string>();
                int    totalItems    = root.TryGetProperty("folderItemCount", out var ficEl)
                                           ? ficEl.GetInt32() : 0;

                if (root.TryGetProperty("folder", out var folderEl))
                {
                    folderName = folderEl.TryGetProperty("name",      out var fnEl) ? (fnEl.GetString() ?? "") : "";
                    ownerName  = folderEl.TryGetProperty("ownerName", out var onEl) ? (onEl.GetString() ?? "") : "";

                    if (folderEl.TryGetProperty("path", out var pathEl) &&
                        pathEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var seg in pathEl.EnumerateArray())
                        {
                            string segId   = seg.TryGetProperty("id",   out var siEl) ? siEl.ToString() : "";
                            string segName = seg.TryGetProperty("name", out var snEl) ? (snEl.GetString() ?? "") : "";
                            if (segId == "0" || string.IsNullOrWhiteSpace(segName)) continue;
                            breadcrumb.Add(segName);
                        }
                    }
                }

                // Use folder ID as fallback display name for root
                string displayName = string.IsNullOrWhiteSpace(folderName)
                    ? (folderId == "0" ? "All Files (Root)" : $"Folder {folderId}")
                    : folderName;

                // Header
                var sb = new StringBuilder();
                sb.AppendLine("╔══════════════════════════════════════════════════╗");
                sb.AppendLine($"  Box Folder : \"{displayName}\"  (ID: {folderId})");
                if (breadcrumb.Count > 0)
                    sb.AppendLine($"  Path       : {string.Join(" › ", breadcrumb)}");
                if (!string.IsNullOrEmpty(ownerName))
                    sb.AppendLine($"  Owner      : {ownerName}");
                sb.AppendLine($"  Items      : {totalItems}");
                sb.AppendLine("╚══════════════════════════════════════════════════╝");
                sb.AppendLine();

                // Items
                if (!root.TryGetProperty("items", out var items) ||
                    items.ValueKind != JsonValueKind.Array ||
                    items.GetArrayLength() == 0)
                {
                    sb.AppendLine("  (folder is empty)");
                    return sb.ToString().TrimEnd();
                }

                int n = 1;
                foreach (var item in items.EnumerateArray())
                {
                    string name      = item.TryGetProperty("name",              out var nmEl) ? (nmEl.GetString()  ?? "") : "";
                    string type      = item.TryGetProperty("type",              out var tpEl) ? (tpEl.GetString()  ?? "") : "";
                    string ext       = item.TryGetProperty("extension",         out var exEl) ? (exEl.GetString()  ?? "") : "";
                    string id        = item.TryGetProperty("id",                out var idEl) ? idEl.ToString()           : "";
                    string updatedBy = item.TryGetProperty("lastUpdatedByName", out var ubEl) ? (ubEl.GetString()  ?? "") : "";

                    long dateUnix = 0;
                    if (item.TryGetProperty("date", out var dateEl))
                    {
                        if      (dateEl.ValueKind == JsonValueKind.Number) dateUnix = dateEl.GetInt64();
                        else if (dateEl.ValueKind == JsonValueKind.String) long.TryParse(dateEl.GetString(), out dateUnix);
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
                    sb.AppendLine("└────────────────────────────────");
                    n++;
                }

                int pageCount  = root.TryGetProperty("pageCount",  out var pcEl) ? pcEl.GetInt32() : 1;
                int pageNumber = root.TryGetProperty("pageNumber", out var pnEl) ? pnEl.GetInt32() : 1;
                if (pageNumber < pageCount)
                {
                    sb.AppendLine();
                    sb.AppendLine($"💡 Showing page {pageNumber} of {pageCount} — additional items exist in this folder.");
                }

                return sb.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                string snippet = result.Body?.Length > 300
                    ? result.Body.Substring(0, 300) + "..."
                    : result.Body ?? "";
                return $"⚠️ box_get_folder — response parse failed: {ex.Message}\n\nRaw (first 300 chars):\n{snippet}";
            }
        }
    }
}
