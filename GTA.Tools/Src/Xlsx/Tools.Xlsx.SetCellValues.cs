using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using GTA.XlsxLib;

namespace GTA.Tools
{
    internal static partial class ToolsXlsx
    {
        [ToolTag("xlsx_local")]
        [FileMutating]
        [ToolInfo(
            name: "xlsx_set_cell_values",
            description:
                "Writes one or more cell values (or formulas) into a LOCAL .xlsx file in a single " +
                "call, then SAVES the result back to disk. Writing to a non-anchor cell inside a " +
                "merged range is automatically redirected to the merge's anchor (top-left) cell — " +
                "mirroring how Excel itself stores merged-range values. A value starting with \"=\" " +
                "is written as a FORMULA (e.g. \"=SUM(A1:A2)\"), not literal text. NOTE: this tool's " +
                "built-in live-recalculation preview does not support newer Excel dynamic-array/spill " +
                "functions (SORT, FILTER, UNIQUE, SEQUENCE, XLOOKUP, etc.) — such a formula is still " +
                "written correctly and will compute fine once the file is opened in real Excel; only " +
                "this tool's own preview of the value will show a #NAME? placeholder with an " +
                "explanatory note, which is NOT a sign the formula is broken. By default the " +
                "modified workbook OVERWRITES the original file at 'path' — pass 'output_path' to " +
                "save as a different file instead. Call xlsx_query_sheets first if you don't already " +
                "know the workbook's sheet names.")]
        [Parameter(index: 0, name: "path",
            type: "string",
            description: "Full absolute Windows path to the local .xlsx file to load and modify.",
            required: true)]
        [Parameter(index: 1, name: "cell_writes",
            type: "string",
            description:
                "JSON array of arrays: each inner array is " +
                "[sheet_name, cell1, value1, cell2, value2, ...] — the sheet name followed by one " +
                "or more (cell, value) pairs to write on that sheet. A value starting with \"=\" is " +
                "treated as a formula. Example: " +
                "[[\"Sheet A\",\"A2\",\"Hello\",\"B2\",\"World\"],[\"Sheet B\",\"C1\",\"=A1+A2\"]].",
            required: true)]
        [Parameter(index: 2, name: "recalculate",
            type: "boolean",
            description:
                "If true (default) and at least one formula was written, recalculates all formulas " +
                "once before saving. Set to false to skip this (e.g. very large workbooks).",
            required: false)]
        [Parameter(index: 3, name: "output_path",
            type: "string",
            description:
                "Optional full absolute Windows path to save the modified workbook to. If omitted, " +
                "the original file at 'path' is overwritten in place.",
            required: false)]
        public static object XlsxSetCellValues(JsonObject input)
        {
            string? err = ToolsXlsxHelpers.LoadLocalXlsx(input, out byte[] bytes, out string path);
            if (err != null) return err;

            string? writesJson = input["cell_writes"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(writesJson))
                return "Error: 'cell_writes' parameter is missing or empty. Expected a JSON array like " +
                       "[[\"Sheet A\",\"A2\",\"Hello\",\"B2\",\"World\"],[\"Sheet B\",\"C1\",\"=A1+A2\"]].";

            List<CellWrite> writes;
            try
            {
                writes = ToolsXlsxHelpers.ParseCellWrites(writesJson);
            }
            catch (Exception ex)
            {
                return $"❌ xlsx_set_cell_values — invalid 'cell_writes' JSON: {ex.Message}";
            }

            if (writes.Count == 0)
                return "Error: 'cell_writes' parsed to zero write instructions.";

            bool    recalculate   = ToolsIOHelpers.GetOptionalBool(input, "recalculate", true);
            string? outputPathRaw = input["output_path"]?.GetValue<string>();

            XlsxOperationResult result = XlsxModifierTool.SetCellValues(bytes, writes, recalculate);
            if (!result.Success || result.ModifiedBytes == null)
                return result.ReportText;

            (string? saveErr, string savedPath) = ToolsXlsxHelpers.SaveLocalXlsx(
                result.ModifiedBytes, path, outputPathRaw);

            return saveErr != null
                ? $"{result.ReportText}\n\n{saveErr}"
                : $"{result.ReportText}\n\n💾 Saved to: {savedPath}";
        }
    }
}
