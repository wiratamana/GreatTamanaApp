using System;
using System.Text.Json.Nodes;
using GTA.DocxLib;
using GTA.Tools.Impl.Common;
using GTA.Tools.Impl.Jira;

namespace GTA.Tools
{
    internal static partial class ToolsJiraApi
    {
        [ToolTag("jira_api")]
        [ToolInfo(
            name: "jira_read_docx",
            description:
                "Download a Word (.docx) JIRA attachment using the JIRA REST API (HTTP Basic " +
                "Auth with an API token), extract all " +
                "document content as structured XML " +
                "(paragraphs, tables, shapes/text-boxes) and feed embedded images directly " +
                "to the AI as base64 image content blocks. " +
                "Provide either the full attachment 'url' or the numeric 'attachment_id'. " +
                "Automatically retries (up to 10×) if the download fails or the file appears " +
                "corrupted/truncated during extraction — a common symptom of flaky proxy/" +
                "internet connections. " +
                "The result is a mixed content array: a structured XML text block describing " +
                "every paragraph, table, and shape in the document, followed by one image " +
                "block per embedded picture. " +
                "Everything runs in-memory — no file is written to disk. " +
                "Uses the OpenXml SDK (no Word/COM required).")]
        [Parameter(index: 0, name: "url",
            type: "string",
            description: "Full JIRA attachment content URL. " +
                         "Example: https://your-company.atlassian.net/rest/api/3/attachment/content/12345. " +
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
        [Parameter(index: 3, name: "timeout_ms",
            type: "integer",
            description: "Download timeout in milliseconds. Default: 60000 (1 minute).",
            required: false)]
        public static object JiraReadDocx(JsonObject input)
        {
            string? url = ResolveJiraAttachmentUrl(input);
            if (string.IsNullOrWhiteSpace(url))
                return "Error: Provide either 'url' (full attachment URL) or 'attachment_id'.";

            int timeoutMs = input["timeout_ms"]?.GetValue<int>() ?? 60_000;

            // Step 1+2: Download DOCX bytes and extract, with retry
            var (success, extraction, lastError) = ToolsCommonPipe.RetryUntilValid(
                attempt: () => JiraDownloadAndExtractDocx(url!, timeoutMs),
                isValid: e => e != null,
                describeInvalid: e => "Extraction returned no result.");

            if (!success || extraction == null)
                return $"❌ jira_read_docx — Failed after {ToolsCommonPipe.MaxDownloadRetries} attempt(s)\n" +
                       $"Error: {lastError}";

            // Step 3: Build JsonArray content blocks for the LLM
            var  content    = new JsonArray();
            int  imageCount = extraction.Images.Count;
            bool hasXml     = !string.IsNullOrWhiteSpace(extraction.XmlContent);

            content.Add(new JsonObject
            {
                ["type"] = "text",
                ["text"] = $"Word document (JIRA attachment) — " +
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
                        "image/jpeg" or "image/jpg" => "image/jpeg",
                        "image/gif"                 => "image/gif",
                        "image/webp"                => "image/webp",
                        _                           => "image/png"   // png + wmf + emf + fallback
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

        private static DocxExtractionResult JiraDownloadAndExtractDocx(string url, int timeoutMs)
        {
            object downloadResult = SendCommandForFileBase64(
                new { command = "DownloadImageAsBase64", url, timeoutMs });

            if (downloadResult is string err1 &&
                err1.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Download failed — {err1}");

            string docxBase64 = (string)downloadResult;

            if (string.IsNullOrWhiteSpace(docxBase64))
                throw new InvalidOperationException(
                    "Download returned empty data (possible corrupted/truncated response).");

            try
            {
                return JiraDocumentExtraction.ExtractDocx(docxBase64);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Extraction failed — the file may be corrupted/truncated: {ex.Message}", ex);
            }
        }
    }
}
