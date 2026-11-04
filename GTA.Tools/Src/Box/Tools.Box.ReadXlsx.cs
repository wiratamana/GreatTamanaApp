using System.Text.Json.Nodes;
using GTA.Tools.Impl.Box;
using GTA.Tools.Impl.Common;

namespace GTA.Tools
{
    internal static partial class ToolsBoxApi
    {
        [ToolTag("box_api")]
        [ToolInfo(
            name: "box_read_xlsx",
            description:
                "Download an Excel (.xlsx) file from Box by file ID using the " +
                "authenticated Box session (pure HTTP) and return its content as clean, " +
                "token-efficient PLAIN TEXT (never raw XML) — cell values, merged " +
                "ranges, text shape content, and image positions. " +
                "Visual formatting (bold/italic/colors/sizes) is stripped to save tokens; " +
                "only cell values, merged ranges, shape text, and image positions are returned. " +
                "Embedded images are NOT loaded as pixel data — their sheet, cell position, " +
                "and size are listed in the output with instructions to call box_read_xlsx_image " +
                "to fetch specific images. " +
                "If the workbook is too large (~100,000+ estimated tokens) the response " +
                "automatically falls back to returning ONLY the list of sheet names plus " +
                "instructions to re-call this tool one sheet at a time using the 'sheet' " +
                "parameter — never give up, one of the sheets is very likely what you need. " +
                "Everything runs in-memory — no file is written to disk.")]
        [Parameter(index: 0, name: "file_id",
            type: "string",
            description: "Numeric Box file ID without the 'f_' prefix. Example: '1234567890123'.",
            required: true)]
        [Parameter(index: 1, name: "sheet",
            type: "string",
            description: "Optional exact (case-insensitive) worksheet name. When provided, only " +
                         "that sheet's content is returned instead of the whole workbook. Use this " +
                         "to read a huge workbook one sheet at a time, or whenever you only need one " +
                         "specific sheet.",
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
        public static object BoxReadXlsx(JsonObject input)
        {
            string? fileId = input["file_id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(fileId))
                return "Error: 'file_id' is required.";

            string? sheet         = input["sheet"]?.GetValue<string>();
            int     timeoutMs     = input["timeout_ms"]?.GetValue<int>() ?? 120_000;
            string? sharedLinkUrl = input["shared_link_url"]?.GetValue<string>();

            // Step 1: Download XLSX bytes from Box in-memory
            var (xlsxBase64, downloadError) = BoxApiClient.FetchBase64(fileId, timeoutMs, sharedLinkUrl);
            if (!string.IsNullOrEmpty(downloadError))
                return $"❌ box_read_xlsx — Download failed\nError: {downloadError}";

            var extraction = BoxDocumentExtraction.ExtractXmlOnly(xlsxBase64!, timeoutMs);

            if (extraction.TimedOut)
            {
                return
                    $"❌ box_read_xlsx — Excel COM extraction timed out after {extraction.TimeoutMsUsed / 1000}s.\n" +
                    $"Box file   : {fileId}\n" +
                    $"File size  : {xlsxBase64!.Length / 1024:N0} KB (base64 encoded)\n\n" +
                    "ROOT CAUSE: Excel COM freezes iterating shape objects on files with many\n" +
                    "embedded images. The spawned EXCEL.EXE process has been killed.\n\n" +
                    "ALTERNATIVES:\n" +
                    "  • box_read_xlsx_image — extracts images only (ClosedXML, no Excel COM)\n" +
                    "  • box_download_file   — download the raw .xlsx for manual inspection";
            }

            if (extraction.Error != null)
                return $"❌ box_read_xlsx — Extraction failed\nError: {extraction.Error}";

            return ToolsCommonPipe.BuildXlsxLlmOutput(
                xmlContent            : extraction.XmlContent ?? string.Empty,
                fileLabel             : $"Box file {fileId}",
                requestedSheet        : sheet,
                toolName              : "box_read_xlsx",
                imageToolName         : "box_read_xlsx_image",
                imageIdParamName      : "file_id",
                imageIdParamValue     : fileId!);
        }
    }
}
