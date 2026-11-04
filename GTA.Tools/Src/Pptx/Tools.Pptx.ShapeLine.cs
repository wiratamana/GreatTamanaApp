using System.Text.Json.Nodes;
using PptxModifierLib;

namespace GTA.Tools
{
    internal static partial class ToolsPptx
    {
        [ToolTag("pptx_local")]
        [FileMutating]
        [ToolInfo(
            name: "pptx_shape_line",
            description:
                "Creates or updates (upserted by 'shape_name') a single straight-line connector " +
                "(no arrowhead) between two literal points on one slide of a LOCAL .pptx file, then " +
                "SAVES the result back to disk. start_x/start_y/end_x/end_y are NORMALIZED " +
                "(0,0)-(1,1) fractions of the slide's actual width/height — (0,0) is the slide's " +
                "top-left corner, (1,1) its bottom-right corner. By default the modified " +
                "presentation OVERWRITES the original file at 'path' — pass 'output_path' to save " +
                "as a different file instead.")]
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
            description: "Stable identifier for this connector. Reusing the same name edits it in place.",
            required: true)]
        [Parameter(index: 3, name: "start_x",
            type: "number",
            description: "Start point X, normalized 0.0-1.0 as a fraction of the slide's actual WIDTH.",
            required: true)]
        [Parameter(index: 4, name: "start_y",
            type: "number",
            description: "Start point Y, normalized 0.0-1.0 as a fraction of the slide's actual HEIGHT.",
            required: true)]
        [Parameter(index: 5, name: "end_x",
            type: "number",
            description: "End point X, normalized as a fraction of the slide's WIDTH.",
            required: true)]
        [Parameter(index: 6, name: "end_y",
            type: "number",
            description: "End point Y, normalized as a fraction of the slide's HEIGHT.",
            required: true)]
        [Parameter(index: 7, name: "line_width_pt",
            type: "number",
            description: "Line width, in points. Must be positive. Default 1.5pt. Not normalized.",
            required: false)]
        [Parameter(index: 8, name: "line_color_hex",
            type: "string",
            description: "Optional line color as hex. If omitted, uses the theme text/outline color default.",
            required: false)]
        [Parameter(index: 9, name: "line_dash_style",
            type: "string",
            description: "\"solid\" (default), \"dash\", \"dashDot\", \"sysDot\", \"sysDash\", \"lgDash\", \"lgDashDot\", or \"lgDashDotDot\".",
            required: false)]
        [Parameter(index: 10, name: "output_path",
            type: "string",
            description: "Optional full absolute Windows path to save to. If omitted, 'path' is overwritten in place.",
            required: false)]
        public static object PptxShapeLine(JsonObject input)
        {
            string? err = ToolsPptxHelpers.LoadLocalPptx(input, out byte[] bytes, out string path);
            if (err != null) return err;

            int? slideNumber = ToolsIOHelpers.GetOptionalInt(input["slide_number"]);
            string? shapeName = input["shape_name"]?.GetValue<string>();
            double? startX = ToolsIOHelpers.GetOptionalDouble(input["start_x"]);
            double? startY = ToolsIOHelpers.GetOptionalDouble(input["start_y"]);
            double? endX = ToolsIOHelpers.GetOptionalDouble(input["end_x"]);
            double? endY = ToolsIOHelpers.GetOptionalDouble(input["end_y"]);

            if (slideNumber == null) return "Error: 'slide_number' parameter is missing.";
            if (string.IsNullOrWhiteSpace(shapeName)) return "Error: 'shape_name' parameter is missing or empty.";
            if (startX == null || startY == null || endX == null || endY == null)
                return "Error: 'start_x'/'start_y'/'end_x'/'end_y' parameters are required.";

            double lineWidthPt = ToolsIOHelpers.GetOptionalDouble(input["line_width_pt"]) ?? 1.5;
            string? lineColorHex = input["line_color_hex"]?.GetValue<string>();
            string lineDashStyle = input["line_dash_style"]?.GetValue<string>() ?? "solid";
            string? outputPathRaw = input["output_path"]?.GetValue<string>();

            PptxOperationResult result = PptxShapeLineTool.SetShapeLine(
                bytes, slideNumber.Value, shapeName, startX.Value, startY.Value, endX.Value, endY.Value,
                lineWidthPt, lineColorHex, lineDashStyle);
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
