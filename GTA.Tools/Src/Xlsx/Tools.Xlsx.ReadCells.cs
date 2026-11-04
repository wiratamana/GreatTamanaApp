using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using GTA.XlsxLib;

namespace GTA.Tools
{
    internal static partial class ToolsXlsx
    {
        [ToolTag("xlsx_local")]
        [ToolInfo(
            name: "xlsx_read_cells",
            description:
                "Reads one or more (sheet, cell) values from a LOCAL .xlsx file in a single call. " +
                "Reading a non-anchor cell inside a merged range automatically resolves to the " +
                "merge anchor's value (never blank). Formula cells show BOTH the formula text and " +
                "the currently-computed value, e.g. \"A3      : =A1+A2 → 3\". If a formula uses a " +
                "newer Excel dynamic-array/spill function (SORT, FILTER, UNIQUE, SEQUENCE, XLOOKUP, " +
                "etc.) this tool's own live-recalculation preview may show \"#NAME?\" with an " +
                "explanatory note instead of the real value — this is NOT a formula error, it computes " +
                "fine once the file is opened in real Excel; only this tool's preview is limited. " +
                "Call xlsx_query_sheets first if you don't already know the workbook's sheet names. " +
                "Read-only — never modifies the file.")]
        [Parameter(index: 0, name: "path",
            type: "string",
            description: "Full absolute Windows path to the local .xlsx file to read from.",
            required: true)]
        [Parameter(index: 1, name: "cell_reads",
            type: "string",
            description:
                "JSON array of arrays: each inner array is [sheet_name, cell1, cell2, ...] — the " +
                "sheet name followed by one or more cell references to read from that sheet. " +
                "Example: [[\"Sheet A\",\"A2\",\"B2\"],[\"Sheet B\",\"C1\"]] reads A2 and B2 from " +
                "\"Sheet A\" plus C1 from \"Sheet B\" in one call.",
            required: true)]
        public static object XlsxReadCells(JsonObject input)
        {
            string? err = ToolsXlsxHelpers.LoadLocalXlsx(input, out byte[] bytes, out string path);
            if (err != null) return err;

            string? cellReadsJson = input["cell_reads"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(cellReadsJson))
                return "Error: 'cell_reads' parameter is missing or empty. Expected a JSON array like " +
                       "[[\"Sheet A\",\"A2\",\"B2\"],[\"Sheet B\",\"C1\"]].";

            List<CellReadRequest> reads;
            try
            {
                reads = ToolsXlsxHelpers.ParseCellReads(cellReadsJson);
            }
            catch (Exception ex)
            {
                return $"❌ xlsx_read_cells — invalid 'cell_reads' JSON: {ex.Message}";
            }

            if (reads.Count == 0)
                return "Error: 'cell_reads' parsed to zero read requests.";

            return XlsxModifierTool.ReadCells(bytes, reads);
        }
    }
}
