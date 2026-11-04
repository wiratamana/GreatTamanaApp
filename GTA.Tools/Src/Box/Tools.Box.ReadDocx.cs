using System;
using System.Text.Json.Nodes;
using GTA.DocxLib;
using GTA.Tools.Impl.Box;

namespace GTA.Tools
{
    internal static partial class ToolsBoxApi
    {
        [ToolTag("box_api")]
        [ToolInfo(
            name: "box_read_docx",
            description:
                "Download a Word (.docx) file from Box by file ID using the " +
                "authenticated Box session (pure HTTP), extract all document content as " +
                "structured XML (paragraphs, tables, shapes/text-boxes) and feed " +
                "embedded images to the AI as base64 image content blocks. " +
                "Everything runs in-memory — no file is written to disk. " +
                "Uses the OpenXml SDK (no Word/COM required).")]
        [Parameter(index: 0, name: "file_id",
            type: "string",
            description: "Numeric Box file ID without the 'f_' prefix. Example: '1234567890'.",
            required: true)]
        [Parameter(index: 1, name: "timeout_ms",
            type: "integer",
            description: "Download timeout in milliseconds. Default: 120000 (2 minutes).",
            required: false)]
        [Parameter(index: 2, name: "shared_link_url",
            type: "string",
            description: "OPTIONAL. Fix for 403 Forbidden errors when file_id was discovered via a " +
                         "Box shared link (https://<tenant>.box.com/s/<token>) rather than by " +
                         "browsing a folder you're a direct collaborator on. Pass the original " +
                         "shared link URL here and the tool will first issue an authenticated GET to it " +
                         "to register the shared-item access grant before downloading. Not needed " +
                         "for files reached via normal folder browsing/search.",
            required: false)]
        public static object BoxReadDocx(JsonObject input)
        {
            string? fileId = input["file_id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(fileId))
                return "Error: 'file_id' is required.";

            int     timeoutMs     = input["timeout_ms"]?.GetValue<int>() ?? 120_000;
            string? sharedLinkUrl = input["shared_link_url"]?.GetValue<string>();

            // Step 1: Download DOCX bytes from Box in-memory
            var (docxBase64, downloadError) = BoxApiClient.FetchBase64(fileId, timeoutMs, sharedLinkUrl);
            if (!string.IsNullOrEmpty(downloadError))
                return $"❌ box_read_docx — Download failed\nError: {downloadError}";

            // Step 2: Extract content via GTA.DocxLib (pure OpenXml, no STA needed)
            DocxExtractionResult extraction;
            try
            {
                extraction = BoxDocumentExtraction.ExtractDocx(docxBase64!, $"BoxFile_{fileId}");
            }
            catch (Exception ex)
            {
                return $"❌ box_read_docx — Extraction failed\nError: {ex.Message}";
            }

            // Step 3: Build JsonArray content blocks for the LLM
            var content    = new JsonArray();
            int imageCount = extraction.Images.Count;
            bool hasXml    = !string.IsNullOrWhiteSpace(extraction.XmlContent);

            content.Add(new JsonObject
            {
                ["type"] = "text",
                ["text"] = $"Word document (Box file {fileId}) — " +
                           $"{extraction.Paragraphs.Count} paragraph(s), " +
                           $"{extraction.Tables.Count} table(s), " +
                           $"{extraction.Shapes.Count} shape(s), " +
                           $"{imageCount} embedded image(s). " +
                           (hasXml ? "Full document XML below." : "No XML extracted.")
            });

            if (hasXml)
            {
                content.Add(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = "--- Document XML ---\n" + extraction.XmlContent
                });
            }

            if (imageCount > 0)
            {
                content.Add(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = $"--- Embedded Images ({imageCount} total) ---"
                });

                for (int i = 0; i < extraction.Images.Count; i++)
                {
                    ImageData img = extraction.Images[i];

                    // Map ContentType to a valid Anthropic media_type
                    string mediaType = img.ContentType.ToLowerInvariant() switch
                    {
                        "image/jpeg" or "image/jpg"  => "image/jpeg",
                        "image/gif"                  => "image/gif",
                        "image/webp"                 => "image/webp",
                        _                            => "image/png"   // png + wmf + emf + fallback
                    };

                    content.Add(new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = $"Image {i + 1}: {img.FileName}  ({img.ContentType}, {img.Bytes.Length:N0} bytes)"
                    });

                    content.Add(new JsonObject
                    {
                        ["type"] = "image",
                        ["source"] = new JsonObject
                        {
                            ["type"]       = "base64",
                            ["media_type"] = mediaType,
                            ["data"]       = Convert.ToBase64String(img.Bytes)
                        }
                    });
                }
            }

            return content;
        }
    }
}
