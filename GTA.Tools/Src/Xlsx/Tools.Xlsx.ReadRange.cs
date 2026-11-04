using System;
using System.Text.Json.Nodes;
using GTA.XlsxLib;

namespace GTA.Tools
{
    internal static partial class ToolsXlsx
    {
        [ToolTag("xlsx_local")]
        [ToolInfo(
            name: "xlsx_read_range",
            description:
                "Reads every non-blank cell inside one explicit cell range (e.g. \"A1:Z10\") on one " +
                "worksheet of a LOCAL .xlsx file in a single call, grouped by row (e.g. \"Row 3:\" " +
                "followed by indented \"B3: value\" lines). Non-anchor cells inside a merged range are " +
                "skipped (their value lives on the merge anchor, which is shown with a \"(merged " +
                "A1:C1)\" note); formula cells show BOTH the formula text and the currently-computed " +
                "value in a trailing \"Formulas:\" section. Call xlsx_query_sheets first if you don't " +
                "already know the workbook's sheet names. Read-only — never modifies the file. " +
                "For the whole sheet instead of one range, use xlsx_read_sheet.")]
        [Parameter(index: 0, name: "path",
            type: "string",
            description: "Full absolute Windows path to the local .xlsx file to read from.",
            required: true)]
        [Parameter(index: 1, name: "sheet",
            type: "string",
            description: "Exact (case-insensitive) worksheet name containing the range.",
            required: true)]
        [Parameter(index: 2, name: "range",
            type: "string",
            description: "Cell range address to read, e.g. \"A1:Z10\" or a single cell like \"B3\".",
            required: true)]
        public static object XlsxReadRange(JsonObject input)
        {
            string? err = ToolsXlsxHelpers.LoadLocalXlsx(input, out byte[] bytes, out string path);
            if (err != null) return err;

            string? sheet = input["sheet"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(sheet))
                return "Error: 'sheet' parameter is missing or empty. Call xlsx_query_sheets first if " +
                       "you don't know the workbook's sheet names.";

            string? range = input["range"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(range))
                return "Error: 'range' parameter is missing or empty. Expected a cell range like \"A1:Z10\".";

            try
            {
                return XlsxModifierTool.ReadRange(bytes, sheet, range);
            }
            catch (Exception ex)
            {
                return $"❌ xlsx_read_range — {ex.Message}";
            }
        }
    }
}
