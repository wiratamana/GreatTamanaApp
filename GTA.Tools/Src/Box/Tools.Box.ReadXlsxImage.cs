using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;
using GTA.Tools.Impl.Box;
using GTA.XlsxLib;

namespace GTA.Tools
{
    internal static partial class ToolsBoxApi
    {
        [ToolTag("box_api")]
        [ToolInfo(
            name: "box_read_xlsx_image",
            description:
                "Download an Excel (.xlsx) file from Box by file ID and work with its " +
                "embedded images in two steps. " +
                "Step 1 — Call without image_indices to receive a manifest listing every " +
                "image with its index number, sheet name, cell address, filename, and byte size. " +
                "No pixel data is sent to the AI at this stage. " +
                "Step 2 — Call again with image_indices (e.g. \"1\" or \"1,3,5\") to load " +
                "specific images as base64 content blocks the AI can see. " +
                "Multiple images can be loaded in a single call. " +
                "Use this after box_read_xlsx to inspect images whose positions are shown " +
                "in the <Images> sections of the workbook XML.")]
        [Parameter(index: 0, name: "file_id",
            type: "string",
            description: "Numeric Box file ID without the 'f_' prefix. Example: '1234567890123'.",
            required: true)]
        [Parameter(index: 1, name: "image_indices",
            type: "string",
            description: "Comma-separated 1-based image index numbers to load, e.g. \"1\" or \"1,3,5\". " +
                         "If omitted, only the image manifest is returned — no pixel data is sent.",
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
        public static object BoxReadXlsxImage(JsonObject input)
        {
            string? fileId = input["file_id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(fileId))
                return "Error: 'file_id' is required.";

            string? indicesStr    = input["image_indices"]?.GetValue<string>();
            int     timeoutMs     = input["timeout_ms"]?.GetValue<int>() ?? 120_000;
            string? sharedLinkUrl = input["shared_link_url"]?.GetValue<string>();

            // Step 1: Download XLSX bytes from Box in-memory
            var (xlsxBase64, downloadError) = BoxApiClient.FetchBase64(fileId, timeoutMs, sharedLinkUrl);
            if (!string.IsNullOrEmpty(downloadError))
                return $"❌ box_read_xlsx_image — Download failed\nError: {downloadError}";

            // Step 2: Extract images only via ClosedXML (no STA needed)
            List<XlsxImageResult> images;
            try
            {
                images = BoxDocumentExtraction.ExtractImagesOnly(xlsxBase64!);
            }
            catch (System.Exception ex)
            {
                return $"❌ box_read_xlsx_image — Image extraction failed\nError: {ex.Message}";
            }

            int totalCount = images.Count;
            if (totalCount == 0)
                return $"ℹ️ box_read_xlsx_image — No embedded images found in Box file {fileId}.";

            // Step 3: Parse requested indices
            HashSet<int>? requestedIndices = null;
            if (!string.IsNullOrWhiteSpace(indicesStr))
            {
                requestedIndices = new HashSet<int>();
                foreach (string part in indicesStr.Split(','))
                    if (int.TryParse(part.Trim(), out int idx) && idx >= 1 && idx <= totalCount)
                        requestedIndices.Add(idx);

                if (requestedIndices.Count == 0)
                    return $"❌ box_read_xlsx_image — No valid indices in \"{indicesStr}\". " +
                           $"Valid range: 1–{totalCount}.";
            }

            // Step 4: Build manifest text
            var manifest = new StringBuilder();
            manifest.AppendLine($"--- Image Manifest (Box file {fileId}) — {totalCount} image(s) ---");
            for (int i = 0; i < images.Count; i++)
            {
                XlsxImageResult img = images[i];
                int  idx      = i + 1;
                bool willLoad = requestedIndices?.Contains(idx) ?? false;
                string marker = willLoad ? "  <- loading" : "";
                manifest.AppendLine(
                    $"[{idx}] Sheet: {img.SheetName}  Cell: {img.CellAddress}  " +
                    $"Name: {img.Name}  ({img.MediaType}, {img.Bytes.Length:N0} bytes){marker}");
            }
            if (requestedIndices == null)
            {
                manifest.AppendLine();
                manifest.AppendLine(
                    "To load images call box_read_xlsx_image again with image_indices, " +
                    "e.g. image_indices: \"1\" or image_indices: \"1,3,5\"");
            }

            // Step 5: Build content array
            var content = new JsonArray();

            content.Add(new JsonObject
            {
                ["type"] = "text",
                ["text"] = requestedIndices != null
                    ? $"Excel workbook (Box file {fileId}) — loading {requestedIndices.Count} of {totalCount} image(s)."
                    : $"Excel workbook (Box file {fileId}) — {totalCount} embedded image(s). Manifest below (no pixel data loaded yet)."
            });

            content.Add(new JsonObject
            {
                ["type"] = "text",
                ["text"] = manifest.ToString().TrimEnd()
            });

            // Step 6: Emit selected image blocks
            if (requestedIndices != null)
            {
                content.Add(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = "--- Loaded Images ---"
                });

                for (int i = 0; i < images.Count; i++)
                {
                    int idx = i + 1;
                    if (!requestedIndices.Contains(idx)) continue;

                    XlsxImageResult img = images[i];
                    content.Add(new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = $"[{idx}] Sheet: {img.SheetName}  Cell: {img.CellAddress} — {img.Name}"
                    });
                    content.Add(new JsonObject
                    {
                        ["type"] = "image",
                        ["source"] = new JsonObject
                        {
                            ["type"]       = "base64",
                            ["media_type"] = img.MediaType,
                            ["data"]       = img.Base64
                        }
                    });
                }
            }

            return content;
        }
    }
}
