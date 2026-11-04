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
            name: "xlsx_create_sheets",
            description:
                "Creates one or more new worksheets by name in a LOCAL .xlsx file in a single call, " +
                "then SAVES the result back to disk. Each name is validated (non-empty, <= 31 chars, " +
                "no \\ / ? * [ ] : characters, must not already exist — including duplicates within " +
                "the same request); an invalid name is rejected with a clear message rather than " +
                "silently auto-renamed. By default the modified workbook OVERWRITES the original " +
                "file at 'path' — pass 'output_path' to save as a different file instead.")]
        [Parameter(index: 0, name: "path",
            type: "string",
            description: "Full absolute Windows path to the local .xlsx file to load and modify.",
            required: true)]
        [Parameter(index: 1, name: "sheet_names",
            type: "string",
            description:
                "JSON array of the new worksheet name(s) to create, e.g. " +
                "[\"New Sheet 1\",\"New Sheet 2\"].",
            required: true)]
        [Parameter(index: 2, name: "output_path",
            type: "string",
            description:
                "Optional full absolute Windows path to save the modified workbook to. If omitted, " +
                "the original file at 'path' is overwritten in place.",
            required: false)]
        public static object XlsxCreateSheets(JsonObject input)
        {
            string? err = ToolsXlsxHelpers.LoadLocalXlsx(input, out byte[] bytes, out string path);
            if (err != null) return err;

            string? namesJson = input["sheet_names"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(namesJson))
                return "Error: 'sheet_names' parameter is missing or empty. Expected a JSON array like " +
                       "[\"New Sheet 1\",\"New Sheet 2\"].";

            List<string> names;
            try
            {
                names = ToolsXlsxHelpers.ParseSheetNames(namesJson);
            }
            catch (Exception ex)
            {
                return $"❌ xlsx_create_sheets — invalid 'sheet_names' JSON: {ex.Message}";
            }

            if (names.Count == 0)
                return "Error: 'sheet_names' parsed to zero sheet names.";

            string? outputPathRaw = input["output_path"]?.GetValue<string>();

            XlsxOperationResult result = XlsxModifierTool.CreateSheets(bytes, names);
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
