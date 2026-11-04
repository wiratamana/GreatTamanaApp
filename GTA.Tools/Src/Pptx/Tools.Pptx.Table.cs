using System;
using System.Text.Json.Nodes;
using PptxModifierLib;

namespace GTA.Tools
{
    internal static partial class ToolsPptx
    {
        [ToolTag("pptx_local")]
        [FileMutating]
        [ToolInfo(
            name: "pptx_table",
            description:
                "Creates or fully replaces (matched by 'shape_name') a table shape on one slide of " +
                "a LOCAL .pptx file, then SAVES the result back to disk. Repeat calls with the SAME " +
                "shape_name replace the ENTIRE table (grid + every row/cell) — there is no partial " +
                "cell-level edit. The bounding box is given as its FOUR EDGES — 'top','left'," +
                "'bottom','right' — NORMALIZED (0,0)-(1,1) fractions of the slide's actual " +
                "width/height (see pptx_shape_rectangle for the full convention); 'column_widths', " +
                "if given, are also normalized fractions of the slide's WIDTH. By default the " +
                "modified presentation OVERWRITES the original file at 'path' — pass 'output_path' " +
                "to save as a different file instead.")]
        [Parameter(index: 0, name: "path",
            type: "string",
            description: "Full absolute Windows path to the local .pptx file to load and modify.",
            required: true)]
        [Parameter(index: 1, name: "slide_number",
            type: "integer",
            description: "1-based slide position (in visual/presentation order).",
            required: true)]
        [Parameter(index: 2, name: "shape_name",
            type: "string",
            description: "Stable identifier for this table. Reusing the same name replaces it in place.",
            required: true)]
        [Parameter(index: 3, name: "top",
            type: "number",
            description: "Top edge, normalized 0.0-1.0 as a fraction of the slide's actual HEIGHT. Must be less than 'bottom'.",
            required: true)]
        [Parameter(index: 4, name: "left",
            type: "number",
            description: "Left edge, normalized 0.0-1.0 as a fraction of the slide's actual WIDTH. Must be less than 'right'.",
            required: true)]
        [Parameter(index: 5, name: "bottom",
            type: "number",
            description: "Bottom edge, normalized 0.0-1.0 as a fraction of the slide's actual HEIGHT. Must be greater than 'top'. The table's total height (bottom - top) is divided evenly across all rows.",
            required: true)]
        [Parameter(index: 6, name: "right",
            type: "number",
            description: "Right edge, normalized 0.0-1.0 as a fraction of the slide's actual WIDTH. Must be greater than 'left'. If 'column_widths' is supplied, its values must sum exactly to (right - left).",
            required: true)]
        [Parameter(index: 7, name: "rows",
            type: "string",
            description:
                "JSON array of arrays of cell text — a rectangular (non-jagged) 2D grid, e.g. " +
                "[[\"Name\",\"Score\"],[\"Alice\",\"92\"],[\"Bob\",\"81\"]]. rows[0] is treated as " +
                "the header row if has_header_row is true. Must contain at least one row and one " +
                "column.",
            required: true)]
        [Parameter(index: 8, name: "has_header_row",
            type: "boolean",
            description:
                "If true (default), rows[0]'s cells are bolded and the table style highlights it as " +
                "a header.",
            required: false)]
        [Parameter(index: 9, name: "column_widths",
            type: "string",
            description:
                "Optional JSON array of explicit per-column widths, normalized as fractions of the " +
                "slide's WIDTH, e.g. [0.2,0.3,0.5]. Length must equal the column count and the " +
                "values must sum exactly to (right - left). If omitted, (right - left) is divided " +
                "evenly across all columns.",
            required: false)]
        [Parameter(index: 10, name: "output_path",
            type: "string",
            description: "Optional full absolute Windows path to save to. If omitted, 'path' is overwritten in place.",
            required: false)]
        public static object PptxTable(JsonObject input)
        {
            string? err = ToolsPptxHelpers.LoadLocalPptx(input, out byte[] bytes, out string path);
            if (err != null) return err;

            int? slideNumber = ToolsIOHelpers.GetOptionalInt(input["slide_number"]);
            string? shapeName = input["shape_name"]?.GetValue<string>();
            double? top = ToolsIOHelpers.GetOptionalDouble(input["top"]);
            double? left = ToolsIOHelpers.GetOptionalDouble(input["left"]);
            double? bottom = ToolsIOHelpers.GetOptionalDouble(input["bottom"]);
            double? right = ToolsIOHelpers.GetOptionalDouble(input["right"]);
            string? rowsJson = input["rows"]?.GetValue<string>();

            if (slideNumber == null) return "Error: 'slide_number' parameter is missing.";
            if (string.IsNullOrWhiteSpace(shapeName)) return "Error: 'shape_name' parameter is missing or empty.";
            if (top == null || left == null || bottom == null || right == null)
                return "Error: 'top'/'left'/'bottom'/'right' parameters are required.";
            if (string.IsNullOrWhiteSpace(rowsJson))
                return "Error: 'rows' parameter is missing or empty. Expected a JSON array of arrays, " +
                       "e.g. [[\"Name\",\"Score\"],[\"Alice\",\"92\"]].";

            string[][] rows;
            try
            {
                rows = ToolsPptxHelpers.ParseTableRows(rowsJson);
            }
            catch (Exception ex)
            {
                return $"❌ pptx_table — invalid 'rows' JSON: {ex.Message}";
            }

            bool hasHeaderRow = ToolsIOHelpers.GetOptionalBool(input, "has_header_row", true);

            double[]? columnWidths = null;
            string? columnWidthsJson = input["column_widths"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(columnWidthsJson))
            {
                try
                {
                    columnWidths = ToolsPptxHelpers.ParseColumnWidths(columnWidthsJson);
                }
                catch (Exception ex)
                {
                    return $"❌ pptx_table — invalid 'column_widths' JSON: {ex.Message}";
                }
            }

            string? outputPathRaw = input["output_path"]?.GetValue<string>();

            PptxOperationResult result = PptxTableTool.SetTable(
                bytes, slideNumber.Value, shapeName, top.Value, left.Value, bottom.Value, right.Value,
                rows, hasHeaderRow, columnWidths);
            if (!result.Success || result.ModifiedBytes == null)
                return result.ReportText;

            (string? saveErr, string savedPath) = ToolsPptxHelpers.SaveLocalPptx(
                result.ModifiedBytes, path, outputPathRaw);

            return saveErr != null
                ? $"{result.ReportText}\n\n{saveErr}"
                : $"{result.ReportText}\n\n💾 Saved to: {savedPath}";
        }
    }
}
