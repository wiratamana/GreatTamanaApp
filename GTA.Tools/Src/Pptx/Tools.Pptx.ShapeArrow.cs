using System.Text.Json.Nodes;
using PptxModifierLib;

namespace GTA.Tools
{
    internal static partial class ToolsPptx
    {
        [ToolTag("pptx_local")]
        [FileMutating]
        [ToolInfo(
            name: "pptx_shape_arrow",
            description:
                "Creates or updates (upserted by 'shape_name') a solid, filled BLOCK ARROW shape " +
                "(thick shape with its own fill/text — not a thin connector line) on one slide of " +
                "a LOCAL .pptx file, then SAVES the result back to disk. The bounding box is the " +
                "UNROTATED box given as its FOUR EDGES — 'top','left','bottom','right' — NORMALIZED " +
                "(0,0)-(1,1) fractions of the slide's actual width/height (see pptx_shape_rectangle " +
                "for the full convention). rotation_degrees controls which way it points: 0=right " +
                "(default), 90=down, 180=left, 270=up. By default the modified presentation " +
                "OVERWRITES the original file at 'path' — pass 'output_path' to save as a different " +
                "file instead.")]
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
            description: "Stable identifier for this shape. Reusing the same name edits it in place.",
            required: true)]
        [Parameter(index: 3, name: "top",
            type: "number",
            description: "Top edge of the unrotated bounding box, normalized 0.0-1.0 as a fraction of the slide's actual HEIGHT. Must be less than 'bottom'.",
            required: true)]
        [Parameter(index: 4, name: "left",
            type: "number",
            description: "Left edge of the unrotated bounding box, normalized 0.0-1.0 as a fraction of the slide's actual WIDTH. Must be less than 'right'.",
            required: true)]
        [Parameter(index: 5, name: "bottom",
            type: "number",
            description: "Bottom edge, normalized 0.0-1.0 as a fraction of the slide's actual HEIGHT. Must be greater than 'top'.",
            required: true)]
        [Parameter(index: 6, name: "right",
            type: "number",
            description: "Right edge, normalized 0.0-1.0 as a fraction of the slide's actual WIDTH. Must be greater than 'left'.",
            required: true)]
        [Parameter(index: 7, name: "rotation_degrees",
            type: "number",
            description: "0=right (default), 90=down, 180=left, 270=up, clockwise. Always applied (pass the same value again on update to keep it unchanged). Not normalized.",
            required: false)]
        [Parameter(index: 8, name: "fill_color_hex",
            type: "string",
            description: "Optional fill color as hex. If omitted: leave existing fill on update / theme accent1 on create.",
            required: false)]
        [Parameter(index: 9, name: "text",
            type: "string",
            description: "Optional plain text. If omitted: leave existing text on update / empty on create.",
            required: false)]
        [Parameter(index: 10, name: "output_path",
            type: "string",
            description: "Optional full absolute Windows path to save to. If omitted, 'path' is overwritten in place.",
            required: false)]
        public static object PptxShapeArrow(JsonObject input)
        {
            string? err = ToolsPptxHelpers.LoadLocalPptx(input, out byte[] bytes, out string path);
            if (err != null) return err;

            int? slideNumber = ToolsIOHelpers.GetOptionalInt(input["slide_number"]);
            string? shapeName = input["shape_name"]?.GetValue<string>();
            double? top = ToolsIOHelpers.GetOptionalDouble(input["top"]);
            double? left = ToolsIOHelpers.GetOptionalDouble(input["left"]);
            double? bottom = ToolsIOHelpers.GetOptionalDouble(input["bottom"]);
            double? right = ToolsIOHelpers.GetOptionalDouble(input["right"]);

            if (slideNumber == null) return "Error: 'slide_number' parameter is missing.";
            if (string.IsNullOrWhiteSpace(shapeName)) return "Error: 'shape_name' parameter is missing or empty.";
            if (top == null || left == null || bottom == null || right == null)
                return "Error: 'top'/'left'/'bottom'/'right' parameters are required.";

            double rotationDegrees = ToolsIOHelpers.GetOptionalDouble(input["rotation_degrees"]) ?? 0;
            string? fillColorHex = input["fill_color_hex"]?.GetValue<string>();
            string? text = input["text"]?.GetValue<string>();
            string? outputPathRaw = input["output_path"]?.GetValue<string>();

            PptxOperationResult result = PptxShapeArrowTool.SetShapeArrow(
                bytes, slideNumber.Value, shapeName, top.Value, left.Value, bottom.Value, right.Value,
                rotationDegrees, fillColorHex, text);
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
