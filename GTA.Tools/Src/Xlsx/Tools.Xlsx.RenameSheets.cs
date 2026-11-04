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
            name: "xlsx_rename_sheets",
            description:
                "Renames one or more existing worksheets in a LOCAL .xlsx file in a single call, " +
                "then SAVES the result back to disk. Renames are processed in REQUEST ORDER. A " +
                "direct two-step swap (A→B, B→A) will fail on the first step if B already exists as " +
                "its own distinct sheet — exactly like real Excel, which never allows two sheets to " +
                "share a name at the same instant; use an intermediate temporary name for a true " +
                "swap. By default the modified workbook OVERWRITES the original file at 'path' — " +
                "pass 'output_path' to save as a different file instead. Call xlsx_query_sheets " +
                "first if you don't already know the workbook's current sheet names.")]
        [Parameter(index: 0, name: "path",
            type: "string",
            description: "Full absolute Windows path to the local .xlsx file to load and modify.",
            required: true)]
        [Parameter(index: 1, name: "sheet_renames",
            type: "string",
            description:
                "JSON array of [oldName, newName] pairs, e.g. " +
                "[[\"OldName1\",\"NewName1\"],[\"OldName2\",\"NewName2\"]].",
            required: true)]
        [Parameter(index: 2, name: "output_path",
            type: "string",
            description:
                "Optional full absolute Windows path to save the modified workbook to. If omitted, " +
                "the original file at 'path' is overwritten in place.",
            required: false)]
        public static object XlsxRenameSheets(JsonObject input)
        {
            string? err = ToolsXlsxHelpers.LoadLocalXlsx(input, out byte[] bytes, out string path);
            if (err != null) return err;

            string? renamesJson = input["sheet_renames"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(renamesJson))
                return "Error: 'sheet_renames' parameter is missing or empty. Expected a JSON array like " +
                       "[[\"OldName1\",\"NewName1\"]].";

            List<SheetRename> renames;
            try
            {
                renames = ToolsXlsxHelpers.ParseSheetRenames(renamesJson);
            }
            catch (Exception ex)
            {
                return $"❌ xlsx_rename_sheets — invalid 'sheet_renames' JSON: {ex.Message}";
            }

            if (renames.Count == 0)
                return "Error: 'sheet_renames' parsed to zero rename instructions.";

            string? outputPathRaw = input["output_path"]?.GetValue<string>();

            XlsxOperationResult result = XlsxModifierTool.RenameSheets(bytes, renames);
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
