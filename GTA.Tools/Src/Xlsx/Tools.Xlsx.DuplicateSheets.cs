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
            name: "xlsx_duplicate_sheets",
            description:
                "Duplicates one or more existing worksheets in a LOCAL .xlsx file — copying cell " +
                "values, formulas, formatting/styles, and merged ranges — into brand-new sheet name(s) " +
                "within the SAME workbook, then SAVES the result back to disk. Use this instead of " +
                "manually re-typing an entire template sheet's structure one (cell, value) pair at a " +
                "time via xlsx_set_cell_values. Each new name is validated (non-empty, <= 31 chars, " +
                "no \\ / ? * [ ] : characters, must not already exist) before copying; an invalid name " +
                "rejects only that one item. Processed in request order, so a later item may duplicate " +
                "a sheet created earlier in the SAME batch. By default the modified workbook OVERWRITES " +
                "the original file at 'path' — pass 'output_path' to save as a different file instead. " +
                "Call xlsx_query_sheets first if you don't already know the workbook's sheet names.")]
        [Parameter(index: 0, name: "path",
            type: "string",
            description: "Full absolute Windows path to the local .xlsx file to load and modify.",
            required: true)]
        [Parameter(index: 1, name: "duplicates",
            type: "string",
            description:
                "JSON array of [sourceSheetName, newSheetName] pairs, e.g. " +
                "[[\"Template\",\"Copy 1\"],[\"Copy 1\",\"Copy 2\"]]. The second example duplicates " +
                "\"Copy 1\" (itself created earlier in the same batch) into \"Copy 2\".",
            required: true)]
        [Parameter(index: 2, name: "output_path",
            type: "string",
            description:
                "Optional full absolute Windows path to save the modified workbook to. If omitted, " +
                "the original file at 'path' is overwritten in place.",
            required: false)]
        public static object XlsxDuplicateSheets(JsonObject input)
        {
            string? err = ToolsXlsxHelpers.LoadLocalXlsx(input, out byte[] bytes, out string path);
            if (err != null) return err;

            string? duplicatesJson = input["duplicates"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(duplicatesJson))
                return "Error: 'duplicates' parameter is missing or empty. Expected a JSON array like " +
                       "[[\"Template\",\"Copy 1\"]].";

            List<SheetDuplicate> ops;
            try
            {
                ops = ToolsXlsxHelpers.ParseSheetDuplicates(duplicatesJson);
            }
            catch (Exception ex)
            {
                return $"❌ xlsx_duplicate_sheets — invalid 'duplicates' JSON: {ex.Message}";
            }

            if (ops.Count == 0)
                return "Error: 'duplicates' parsed to zero duplication instructions.";

            string? outputPathRaw = input["output_path"]?.GetValue<string>();

            XlsxOperationResult result = XlsxModifierTool.DuplicateSheets(bytes, ops);
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
