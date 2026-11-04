using System;
using System.IO;
using System.Text.Json.Nodes;
using GTA.Tools.Impl.Box;

namespace GTA.Tools
{
    internal static partial class ToolsBoxApi
    {
        [ToolTag("box_api")]
        [FileMutating]
        [ToolInfo(
            name: "box_download_file",
            description:
                "Download a Box file using the authenticated /index.php form-POST endpoint " +
                "(discovered via browser network inspection), executed over a pure-HTTP " +
                "ADFS/SAML-authenticated Box session. " +
                "POSTs to /index.php?rm=box_v2_download_file&file_id=f_{file_id} with " +
                "Content-Type: application/x-www-form-urlencoded and body request_token=<csrf>. " +
                "Session cookies are injected automatically — no Bearer token needed. " +
                "The downloaded file is ALWAYS written to disk (to save_path if provided, " +
                "otherwise to an auto-generated path under the user's Downloads folder), and " +
                "this tool returns ONLY metadata about the download (file id, saved path, " +
                "content-type, size, HTTP status) — the raw file bytes/base64 are never " +
                "returned to the caller, to avoid exploding LLM token usage. " +
                "Use this tool when box_download_file returns 404 or yields no download URL. " +
                "Supports any file type (.xlsx, .pdf, .pptx, .docx, images, etc.).")]
        [Parameter(index: 0, name: "file_id",
            type: "string",
            description: "Numeric Box file ID without the 'f_' prefix. Example: '1234567890123'.",
            required: true)]
        [Parameter(index: 1, name: "save_path",
            type: "string",
            description: "Full Windows path where the file should be saved. " +
                         "Example: 'C:\\\\Users\\\\<user>\\\\Downloads\\\\report.xlsx'. " +
                         "If omitted, a path is auto-generated under the user's Downloads " +
                         "folder using the file_id and an extension guessed from the " +
                         "response Content-Type. The file is always saved to disk; only " +
                         "metadata about it is returned — the file content itself is never " +
                         "returned inline.",
            required: false)]
        [Parameter(index: 2, name: "timeout_ms",
            type: "integer",
            description: "Download timeout in milliseconds. Default: 120000 (2 minutes).",
            required: false)]
        [Parameter(index: 3, name: "shared_link_url",
            type: "string",
            description: "OPTIONAL. Fix for 403 Forbidden errors when file_id was discovered via a " +
                         "Box shared link (https://<tenant>.box.com/s/<token>) rather than by " +
                         "browsing a folder you're a direct collaborator on. Pass the original " +
                         "shared link URL here and the tool will first issue an authenticated GET to it " +
                         "to register the shared-item access grant before downloading. Not needed " +
                         "for files reached via normal folder browsing/search.",
            required: false)]
        public static object BoxDownloadFile(JsonObject input)
        {
            string? fileId = input["file_id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(fileId))
                return "Error: 'file_id' is required.";

            string? savePath      = input["save_path"]?.GetValue<string>();
            string? sharedLinkUrl = input["shared_link_url"]?.GetValue<string>();
            int     timeoutMsIn   = input["timeout_ms"]?.GetValue<int>() ?? 120_000;
            // timeout_ms is enforced per-attempt by BoxAdfsLoginClient via a
            // CancellationTokenSource — see BoxApiClient.DownloadFileBytes.

            // Download bytes via BoxApiClient (handles CSRF/session/shared-link
            // grant registration/retry internally) ──────────────────────────────
            var (fileBytes, contentType, httpStatus, downloadError) =
                BoxApiClient.DownloadFileBytes(fileId, timeoutMsIn, sharedLinkUrl);

            if (downloadError != null)
                return $"❌ box_download_file — Download failed\nError: {downloadError}";

            if (fileBytes == null || fileBytes.Length == 0)
                return $"❌ box_download_file\nError: Empty file data. HTTP {httpStatus}.";

            string ct = contentType ?? "application/octet-stream";

            if (string.IsNullOrWhiteSpace(savePath))
            {
                string downloadsDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Downloads");
                savePath = Path.Combine(downloadsDir, $"box_{fileId}{BoxApiClient.GuessExtensionFromContentType(ct)}");
            }

            // Write bytes to disk
            try
            {
                string? dir = Path.GetDirectoryName(savePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllBytes(savePath, fileBytes);

                return
                    $"✅ box_download_file — File saved to disk\n" +
                    $"File ID      : {fileId}\n" +
                    $"Saved to     : {savePath}\n" +
                    $"Content-Type : {ct}\n" +
                    $"Size (bytes) : {fileBytes.Length:N0}\n" +
                    $"HTTP Status  : {httpStatus}\n\n" +
                    $"💡 To read the file content use:\n" +
                    $"   • Excel .xlsx    →  xlsx_* tools (see get_available_tools category 'xlsx')\n" +
                    $"   • PowerPoint     →  pptx_* tools (see get_available_tools category 'pptx')\n" +
                    $"   • Image          →  load_image(path: \"{savePath}\")\n" +
                    $"   • Any binary     →  read_file_base64(path: \"{savePath}\")";
            }
            catch (Exception ex)
            {
                return $"❌ box_download_file\nError saving to '{savePath}': {ex.Message}";
            }
        }
    }
}
