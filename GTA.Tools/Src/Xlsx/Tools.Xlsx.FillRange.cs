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
            name: "xlsx_fill_range",
            description:
                "Writes one or more rectangular blocks of values (2-D grids) into a LOCAL .xlsx file " +
                "in a single call, then SAVES the result back to disk. Each grid is anchored at a " +
                "single top-left cell — use this instead of enumerating hundreds/thousands of " +
                "individual (cell, value) pairs via xlsx_set_cell_values when pasting a whole table/" +
                "block of data at once. A grid value starting with \"=\" is written as a FORMULA " +
                "(same convention as xlsx_set_cell_values), everything else as literal text. By " +
                "default the modified workbook OVERWRITES the original file at 'path' — pass " +
                "'output_path' to save as a different file instead. Call xlsx_query_sheets first if " +
                "you don't already know the workbook's sheet names.")]
        [Parameter(index: 0, name: "path",
            type: "string",
            description: "Full absolute Windows path to the local .xlsx file to load and modify.",
            required: true)]
        [Parameter(index: 1, name: "fills",
            type: "string",
            description:
                "JSON array of [sheet, anchorCell, grid] entries — grid is a JSON array of arrays of " +
                "strings (row-major: each inner array is one row). Cell (anchorRow + r, anchorCol + c) " +
                "receives grid[r][c]. Example: [[\"Sheet A\",\"B2\",[[\"Name\",\"Age\"],[\"John\",\"30\"]," +
                "[\"Jane\",\"25\"]]]] writes a 3-row x 2-column block starting at B2. A grid string " +
                "starting with \"=\" is written as a formula. Ragged rows (different lengths) are " +
                "supported — shorter rows simply leave their trailing columns untouched.",
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
        public static object XlsxFillRange(JsonObject input)
        {
            string? err = ToolsXlsxHelpers.LoadLocalXlsx(input, out byte[] bytes, out string path);
            if (err != null) return err;

            string? fillsJson = input["fills"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(fillsJson))
                return "Error: 'fills' parameter is missing or empty. Expected a JSON array like " +
                       "[[\"Sheet A\",\"B2\",[[\"h1\",\"h2\"],[\"v1\",\"v2\"]]]].";

            List<RangeFill> fills;
            try
            {
                fills = ToolsXlsxHelpers.ParseRangeFills(fillsJson);
            }
            catch (Exception ex)
            {
                return $"❌ xlsx_fill_range — invalid 'fills' JSON: {ex.Message}";
            }

            if (fills.Count == 0)
                return "Error: 'fills' parsed to zero range fill instructions.";

            bool    recalculate   = ToolsIOHelpers.GetOptionalBool(input, "recalculate", true);
            string? outputPathRaw = input["output_path"]?.GetValue<string>();

            XlsxOperationResult result = XlsxModifierTool.FillRange(bytes, fills, recalculate);
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
