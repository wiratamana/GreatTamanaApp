using System;
using System.Text.Json.Nodes;
using GTA.XlsxLib;

namespace GTA.Tools
{
    internal static partial class ToolsXlsx
    {
        [ToolTag("xlsx_local")]
        [ToolInfo(
            name: "xlsx_read_sheet",
            description:
                "Reads the ENTIRE content of one worksheet from a LOCAL .xlsx file in a single call " +
                "— every non-blank cell in the sheet's used range, grouped by row (e.g. \"Row 3:\" " +
                "followed by indented \"B3: value\" lines). Non-anchor cells inside a merged range are " +
                "skipped (their value lives on the merge anchor, which is shown with a \"(merged " +
                "A1:C1)\" note); formula cells show BOTH the formula text and the currently-computed " +
                "value in a trailing \"Formulas:\" section. Call xlsx_query_sheets first if you don't " +
                "already know the workbook's sheet names. Read-only — never modifies the file. " +
                "For a specific sub-range instead of the whole sheet, use xlsx_read_range.")]
        [Parameter(index: 0, name: "path",
            type: "string",
            description: "Full absolute Windows path to the local .xlsx file to read from.",
            required: true)]
        [Parameter(index: 1, name: "sheet",
            type: "string",
            description: "Exact (case-insensitive) worksheet name to read in full.",
            required: true)]
        public static object XlsxReadSheet(JsonObject input)
        {
            string? err = ToolsXlsxHelpers.LoadLocalXlsx(input, out byte[] bytes, out string path);
            if (err != null) return err;

            string? sheet = input["sheet"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(sheet))
                return "Error: 'sheet' parameter is missing or empty. Call xlsx_query_sheets first if " +
                       "you don't know the workbook's sheet names.";

            try
            {
                return XlsxModifierTool.ReadSheet(bytes, sheet);
            }
            catch (Exception ex)
            {
                return $"❌ xlsx_read_sheet — {ex.Message}";
            }
        }
    }
}
