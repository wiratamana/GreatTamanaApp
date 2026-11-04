using System;
using System.Text.Json.Nodes;
using System.Threading;
using GTA.XlsxLib;
using GTA.Tools.Impl.Common;
using GTA.Tools.Impl.Jira;

namespace GTA.Tools
{
    internal static partial class ToolsJiraApi
    {
        private const int JiraXlsxOuterRetries = 3;

        [ToolTag("jira_api")]
        [ToolInfo(
            name: "jira_read_xlsx",
            description:
                "Download an Excel (.xlsx) JIRA attachment using the JIRA REST API (HTTP Basic " +
                "Auth with an API token) and return its " +
                "content as clean, token-efficient PLAIN TEXT " +
                "(never raw XML) — cell values, merged ranges, text shape content, and image " +
                "positions. " +
                "Provide either the full attachment 'url' or the numeric 'attachment_id'. " +
                "Automatically retries if the download fails or extraction produces no usable " +
                "content — a common symptom of flaky proxy/internet connections corrupting the " +
                "downloaded file. " +
                "Embedded images are NOT loaded as pixel data — their sheet, cell position, " +
                "and size are listed in the output footer with instructions to call " +
                "jira_read_xlsx_image to fetch specific images. " +
                "If the workbook is too large (~100,000+ estimated tokens) the response " +
                "automatically falls back to returning ONLY the list of sheet names plus " +
                "instructions to re-call this tool one sheet at a time using the 'sheet' " +
                "parameter — never give up, one of the sheets is very likely what you need. " +
                "Everything runs in-memory — no file is written to disk.")]
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
        [Parameter(index: 3, name: "sheet",
            type: "string",
            description: "Optional exact (case-insensitive) worksheet name. When provided, only " +
                         "that sheet's content is returned instead of the whole workbook. Use this " +
                         "to read a huge workbook one sheet at a time, or whenever you only need one " +
                         "specific sheet.",
            required: false)]
        [Parameter(index: 4, name: "timeout_ms",
            type: "integer",
            description: "Download timeout in milliseconds. Default: 60000 (1 minute).",
            required: false)]
        [Parameter(index: 5, name: "row_start",
            type: "integer",
            description: "Optional 0-based first Excel row to include (0 = Excel row 1, matching the " +
                         "same convention as edit_line's line_index). Only meaningful together with " +
                         "'sheet' (or applied per-sheet across the whole workbook if 'sheet' is " +
                         "omitted). Default: 0 (start of sheet). Use this plus 'row_length' to read a " +
                         "huge sheet in deliberate slices instead of risking a silent cutoff.",
            required: false)]
        [Parameter(index: 6, name: "row_length",
            type: "integer",
            description: "Optional number of Excel rows to include starting at 'row_start'. Default: " +
                         "omitted, meaning everything from 'row_start' to the end of the sheet. If the " +
                         "requested range runs past the sheet's actual last row (across cell data, " +
                         "shapes, and images), it is AUTOMATICALLY CLAMPED — never an error, it just " +
                         "returns fewer rows than asked for.",
            required: false)]
        public static object JiraReadXlsx(JsonObject input)
        {
            string? url = ResolveJiraAttachmentUrl(input);
            if (string.IsNullOrWhiteSpace(url))
                return "Error: Provide either 'url' (full attachment URL) or 'attachment_id'.";

            string? sheet     = input["sheet"]?.GetValue<string>();
            int     timeoutMs = input["timeout_ms"]?.GetValue<int>() ?? 60_000;
            int     rowStart  = input["row_start"]?.GetValue<int>() ?? 0;
            int?    rowLength = input["row_length"]?.GetValue<int>();

            // Step 1+2: Download XLSX bytes and extract XML, with retry
            var (success, xmlContent, lastError) = ToolsCommonPipe.RetryUntilValid(
                attempt: () => JiraDownloadAndExtractXlsxXml(url!, timeoutMs),
                isValid: xml => !string.IsNullOrWhiteSpace(xml),
                describeInvalid: xml => "Extraction succeeded but produced no XML content " +
                    "(the file may be corrupted/truncated).",
                maxRetries: JiraXlsxOuterRetries);

            if (!success)
                return $"❌ jira_read_xlsx — Failed after {JiraXlsxOuterRetries} attempt(s)\n" +
                       $"Error: {lastError}";

            return ToolsCommonPipe.BuildXlsxLlmOutput(
                xmlContent            : xmlContent ?? string.Empty,
                fileLabel             : "JIRA attachment",
                requestedSheet        : sheet,
                toolName              : "jira_read_xlsx",
                imageToolName         : "jira_read_xlsx_image",
                imageIdParamName      : "url",
                imageIdParamValue     : url,
                rowStart              : rowStart,
                rowLength             : rowLength);
        }

        private static string JiraDownloadAndExtractXlsxXml(string url, int timeoutMs)
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

            return JiraDocumentExtraction.ExtractXlsxXmlOnly(xlsxBase64);
        }
    }
}
