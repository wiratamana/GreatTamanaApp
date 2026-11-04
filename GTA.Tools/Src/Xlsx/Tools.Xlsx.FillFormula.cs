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
            name: "xlsx_fill_formula",
            description:
                "Expands ONE formula template across every cell of a target range in a LOCAL .xlsx " +
                "file in a single call, then SAVES the result back to disk — the \"fill down/across\" " +
                "counterpart to xlsx_fill_range. The template may reference \"{row}\" (replaced per-cell " +
                "with that cell's 1-based row number) and/or \"{col}\" (replaced with that cell's column " +
                "letter, e.g. \"AB\"), so ONE template like " +
                "\"=IF(AND(C{row}<>\\\"\\\",D{row}<>\\\"\\\"),CONCAT(C{row},\\\"_\\\",D{row}),\\\"\\\")\" " +
                "auto-expands into a correctly row-relative formula for every cell in the range — use " +
                "this instead of pre-computing and enumerating every row-adjusted formula by hand via " +
                "xlsx_set_cell_values. A single-column range (e.g. \"Z8:Z115\") fills straight down; a " +
                "single-row range (e.g. \"Z8:AC8\") fills across; a full 2-D range fills both directions " +
                "at once. NOTE: this tool's built-in live-recalculation preview does not support newer " +
                "Excel dynamic-array functions (SORT, FILTER, UNIQUE, SEQUENCE, XLOOKUP, etc.) — such a " +
                "formula is still written correctly and will compute fine once the file is opened in " +
                "real Excel; only this tool's own preview of the value will show a #NAME? placeholder " +
                "with an explanatory note. By default the modified workbook OVERWRITES the original " +
                "file at 'path' — pass 'output_path' to save as a different file instead. Call " +
                "xlsx_query_sheets first if you don't already know the workbook's sheet names.")]
        [Parameter(index: 0, name: "path",
            type: "string",
            description: "Full absolute Windows path to the local .xlsx file to load and modify.",
            required: true)]
        [Parameter(index: 1, name: "fills",
            type: "string",
            description:
                "JSON array of [sheet, rangeAddress, formulaTemplate] entries. Example: " +
                "[[\"Sheet A\",\"Z8:Z115\",\"=IF(AND(C{row}<>\\\"\\\",D{row}<>\\\"\\\")," +
                "CONCAT(C{row},\\\"_\\\",D{row}),\\\"\\\")\"]]. A leading \"=\" on the template is " +
                "optional and stripped automatically.",
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
        public static object XlsxFillFormula(JsonObject input)
        {
            string? err = ToolsXlsxHelpers.LoadLocalXlsx(input, out byte[] bytes, out string path);
            if (err != null) return err;

            string? fillsJson = input["fills"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(fillsJson))
                return "Error: 'fills' parameter is missing or empty. Expected a JSON array like " +
                       "[[\"Sheet A\",\"Z8:Z115\",\"=CONCAT(C{row},\\\"_\\\",D{row})\"]].";

            List<FormulaFill> fills;
            try
            {
                fills = ToolsXlsxHelpers.ParseFormulaFills(fillsJson);
            }
            catch (Exception ex)
            {
                return $"❌ xlsx_fill_formula — invalid 'fills' JSON: {ex.Message}";
            }

            if (fills.Count == 0)
                return "Error: 'fills' parsed to zero formula fill instructions.";

            bool    recalculate   = ToolsIOHelpers.GetOptionalBool(input, "recalculate", true);
            string? outputPathRaw = input["output_path"]?.GetValue<string>();

            XlsxOperationResult result = XlsxModifierTool.FillFormula(bytes, fills, recalculate);
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
