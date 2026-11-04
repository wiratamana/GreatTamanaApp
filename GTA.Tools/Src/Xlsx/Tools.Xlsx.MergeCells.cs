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
            name: "xlsx_merge_cells",
            description:
                "Merges and/or unmerges one or more cell ranges (e.g. \"A1:C3\") across one or more " +
                "sheets in a LOCAL .xlsx file in a single call, then SAVES the result back to disk. " +
                "Provide 'ranges_to_merge' and/or 'ranges_to_unmerge' — at least one is required; " +
                "both may be used together in the same call. By default the modified workbook " +
                "OVERWRITES the original file at 'path' — pass 'output_path' to save as a different " +
                "file instead. Call xlsx_query_sheets first if you don't already know the workbook's " +
                "sheet names.")]
        [Parameter(index: 0, name: "path",
            type: "string",
            description: "Full absolute Windows path to the local .xlsx file to load and modify.",
            required: true)]
        [Parameter(index: 1, name: "ranges_to_merge",
            type: "string",
            description:
                "JSON array of [sheet, rangeAddress] pairs to MERGE, e.g. " +
                "[[\"Sheet A\",\"A1:C3\"],[\"Sheet B\",\"B2:D4\"]]. Optional if " +
                "'ranges_to_unmerge' is provided instead.",
            required: false)]
        [Parameter(index: 2, name: "ranges_to_unmerge",
            type: "string",
            description:
                "JSON array of [sheet, rangeAddress] pairs to UNMERGE, e.g. " +
                "[[\"Sheet A\",\"E1:E2\"]]. Optional if 'ranges_to_merge' is provided instead.",
            required: false)]
        [Parameter(index: 3, name: "output_path",
            type: "string",
            description:
                "Optional full absolute Windows path to save the modified workbook to. If omitted, " +
                "the original file at 'path' is overwritten in place.",
            required: false)]
        public static object XlsxMergeCells(JsonObject input)
        {
            string? err = ToolsXlsxHelpers.LoadLocalXlsx(input, out byte[] bytes, out string path);
            if (err != null) return err;

            string? mergeJson   = input["ranges_to_merge"]?.GetValue<string>();
            string? unmergeJson = input["ranges_to_unmerge"]?.GetValue<string>();

            if (string.IsNullOrWhiteSpace(mergeJson) && string.IsNullOrWhiteSpace(unmergeJson))
                return "Error: provide at least one of 'ranges_to_merge' or 'ranges_to_unmerge' " +
                       "(JSON array of [sheet, rangeAddress] pairs, e.g. [[\"Sheet A\",\"A1:C3\"]]).";

            var ops = new List<MergeOp>();
            try
            {
                if (!string.IsNullOrWhiteSpace(mergeJson))
                    ops.AddRange(ToolsXlsxHelpers.ParseMergeOps(mergeJson, merge: true));
                if (!string.IsNullOrWhiteSpace(unmergeJson))
                    ops.AddRange(ToolsXlsxHelpers.ParseMergeOps(unmergeJson, merge: false));
            }
            catch (Exception ex)
            {
                return $"❌ xlsx_merge_cells — invalid range JSON: {ex.Message}";
            }

            if (ops.Count == 0)
                return "Error: 'ranges_to_merge'/'ranges_to_unmerge' parsed to zero operations.";

            string? outputPathRaw = input["output_path"]?.GetValue<string>();

            XlsxOperationResult result = XlsxModifierTool.MergeCells(bytes, ops);
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
