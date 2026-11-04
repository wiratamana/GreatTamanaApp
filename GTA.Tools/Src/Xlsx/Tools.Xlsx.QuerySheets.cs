using System;
using System.Text.Json.Nodes;
using GTA.XlsxLib;

namespace GTA.Tools
{
    internal static partial class ToolsXlsx
    {
        [ToolTag("xlsx_local")]
        [ToolInfo(
            name: "xlsx_query_sheets",
            description:
                "Lists every worksheet in a LOCAL .xlsx file (name + a quick used-range summary, " +
                "or \"(empty)\"). This is the DISCOVERY step — call it first when you don't yet " +
                "know a workbook's sheet names, before using xlsx_read_cells, xlsx_set_cell_values, " +
                "xlsx_create_sheets, xlsx_rename_sheets, or xlsx_merge_cells on the same file. " +
                "Everything runs in-memory (ClosedXML) — the file is only read, never modified.")]
        [Parameter(index: 0, name: "path",
            type: "string",
            description: "Full absolute Windows path to the local .xlsx file to inspect.",
            required: true)]
        public static object XlsxQuerySheets(JsonObject input)
        {
            string? err = ToolsXlsxHelpers.LoadLocalXlsx(input, out byte[] bytes, out string path);
            if (err != null) return err;

            try
            {
                return XlsxModifierTool.QuerySheets(bytes);
            }
            catch (Exception ex)
            {
                return $"❌ xlsx_query_sheets — {ex.Message}";
            }
        }
    }
}
