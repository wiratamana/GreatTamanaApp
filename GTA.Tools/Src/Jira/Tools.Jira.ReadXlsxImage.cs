using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;
using GTA.XlsxLib;
using GTA.Tools.Impl.Common;
using GTA.Tools.Impl.Jira;

namespace GTA.Tools
{
    internal static partial class ToolsJiraApi
    {
        [ToolTag("jira_api")]
        [ToolInfo(
            name: "jira_read_xlsx_image",
            description:
                "Download an Excel (.xlsx) JIRA attachment and work with its embedded images " +
                "in two steps. " +
                "Step 1 — Call without image_indices to receive a manifest listing every image " +
                "with its index number, sheet name, cell address, filename, and byte size. " +
                "No pixel data is sent to the AI at this stage. " +
                "Step 2 — Call again with image_indices (e.g. \"1\" or \"1,3,5\") to load " +
                "specific images as base64 content blocks the AI can see. " +
                "Multiple images can be loaded in a single call. " +
                "Automatically retries if the download fails or the file appears corrupted/" +
                "truncated during extraction — a common symptom of flaky proxy/internet " +
                "connections. " +
                "Use this after jira_read_xlsx to inspect images whose positions are shown " +
                "in the <Images> sections of the workbook XML. " +
                "Uses the OpenXml SDK (no Excel/COM required). " +
                "Everything runs in-memory — no file is written to disk.")]
        [Parameter(index: 0, name: "url",
            type: "string",
            description: "Full JIRA attachment content URL. " +
                         "Either 'url' or 'attachment_id' is required.",
            required: false)]
        [Parameter(index: 1, name: "attachment_id",
            type: "string",
            description: "JIRA attachment numeric ID. The URL is constructed automatically. " +
                         "Either 'url' or 'attachment_id' is required.",
            required: false)]
        [Parameter(index: 2, name: "jira_base_url",
            type: "string",
            description: "JIRA instance base URL. Only used when 'attachment_id' is provided. " +
                         "Default: the Jira base URL configured in init.ini.",
            required: false)]
        [Parameter(index: 3, name: "image_indices",
            type: "string",
            description: "Comma-separated 1-based image index numbers to load, e.g. \"1\" or \"1,3,5\". " +
                         "If omitted, only the image manifest is returned — no pixel data is sent.",
            required: false)]
        [Parameter(index: 4, name: "timeout_ms",
            type: "integer",
            description: "Download timeout in milliseconds. Default: 60000 (1 minute).",
            required: false)]
        public static object JiraReadXlsxImage(JsonObject input)
        {
            string? url = ResolveJiraAttachmentUrl(input);
            if (string.IsNullOrWhiteSpace(url))
                return "Error: Provide either 'url' (full attachment URL) or 'attachment_id'.";

            string? indicesStr = input["image_indices"]?.GetValue<string>();
            int     timeoutMs  = input["timeout_ms"]?.GetValue<int>() ?? 60_000;

            // Step 1+2: Download XLSX bytes and extract images, with retry
            var (success, images, lastError) = ToolsCommonPipe.RetryUntilValid(
                attempt: () => JiraDownloadAndExtractXlsxImages(url!, timeoutMs),
                isValid: _ => true, // an empty image list is a legitimate result — only
                                    // exceptions (download/extraction failure) are invalid
                describeInvalid: _ => "unexpected",
                maxRetries: JiraXlsxOuterRetries);

            if (!success || images == null)
                return $"❌ jira_read_xlsx_image — Failed after {JiraXlsxOuterRetries} attempt(s)\n" +
                       $"Error: {lastError}";

            int totalCount = images.Count;
            if (totalCount == 0)
                return "ℹ️ jira_read_xlsx_image — No embedded images found in this workbook.";

            // Step 3: Parse requested indices
            HashSet<int>? requestedIndices = null;
            if (!string.IsNullOrWhiteSpace(indicesStr))
            {
                requestedIndices = new HashSet<int>();
                foreach (string part in indicesStr.Split(','))
                    if (int.TryParse(part.Trim(), out int idx) && idx >= 1 && idx <= totalCount)
                        requestedIndices.Add(idx);

                if (requestedIndices.Count == 0)
                    return $"❌ jira_read_xlsx_image — No valid indices in \"{indicesStr}\". " +
                           $"Valid range: 1–{totalCount}.";
            }

            // Step 4: Build manifest text
            var manifest = new StringBuilder();
            manifest.AppendLine($"--- Image Manifest (JIRA attachment) — {totalCount} image(s) ---");
            for (int i = 0; i < images.Count; i++)
            {
                XlsxImageResult img = images[i];
                int    idx      = i + 1;
                bool   willLoad = requestedIndices?.Contains(idx) ?? false;
                string marker   = willLoad ? "  <- loading" : "";
                manifest.AppendLine(
                    $"[{idx}] Sheet: {img.SheetName}  Cell: {img.CellAddress}  " +
                    $"Name: {img.Name}  ({img.MediaType}, {img.Bytes.Length:N0} bytes){marker}");
            }
            if (requestedIndices == null)
            {
                manifest.AppendLine();
                manifest.AppendLine(
                    "To load images call jira_read_xlsx_image again with image_indices, " +
                    "e.g. image_indices: \"1\" or image_indices: \"1,3,5\"");
            }

            // Step 5: Build content array
            var content = new JsonArray();

            content.Add(new JsonObject
            {
                ["type"] = "text",
                ["text"] = requestedIndices != null
                    ? $"Excel workbook (JIRA attachment) — loading {requestedIndices.Count} of {totalCount} image(s)."
                    : $"Excel workbook (JIRA attachment) — {totalCount} embedded image(s). " +
                      $"Manifest below (no pixel data loaded yet)."
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

        private static List<XlsxImageResult> JiraDownloadAndExtractXlsxImages(string url, int timeoutMs)
        {
            object downloadResult = SendCommandForFileBase64WithRetry(
                new { command = "DownloadImageAsBase64", url, timeoutMs });

            if (downloadResult is string err1 &&
                err1.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Download failed — {err1}");

            string xlsxBase64 = (string)downloadResult;

            if (string.IsNullOrWhiteSpace(xlsxBase64))
                throw new InvalidOperationException(
                    "Download returned empty data (possible corrupted/truncated response).");

            try
            {
                return JiraDocumentExtraction.ExtractXlsxImages(xlsxBase64);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Image extraction failed — the file may be corrupted/truncated: {ex.Message}", ex);
            }
        }
    }
}
