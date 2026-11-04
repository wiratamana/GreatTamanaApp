using System.Text.Json.Nodes;
using PptxModifierLib;

namespace GTA.Tools
{
    internal static partial class ToolsPptx
    {
        [ToolTag("pptx_local")]
        [FileMutating]
        [ToolInfo(
            name: "pptx_shape_rectangle",
            description:
                "Creates or updates (upserted by 'shape_name') a plain filled rectangle shape on " +
                "one slide of a LOCAL .pptx file, then SAVES the result back to disk. The bounding " +
                "box is given as its FOUR EDGES — 'top','left','bottom','right' — NORMALIZED " +
                "(0,0)-(1,1) fractions of the slide's actual width/height — (0,0) is the slide's " +
                "top-left corner, (1,1) its bottom-right corner, independent of the slide's real " +
                "physical size — e.g. top=0.5,left=0.5,bottom=1.0,right=1.0 always fills the exact " +
                "bottom-right quadrant of the slide. Calling this again with the SAME shape_name on " +
                "the SAME slide edits that rectangle in place; a new shape_name creates a brand-new " +
                "one. By default the modified presentation OVERWRITES the original file at 'path' — " +
                "pass 'output_path' to save as a different file instead.")]
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
            description: "Stable identifier for this rectangle. Reusing the same name edits it in place.",
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
            description: "Bottom edge, normalized 0.0-1.0 as a fraction of the slide's actual HEIGHT. Must be greater than 'top'.",
            required: true)]
        [Parameter(index: 6, name: "right",
            type: "number",
            description: "Right edge, normalized 0.0-1.0 as a fraction of the slide's actual WIDTH. Must be greater than 'left'.",
            required: true)]
        [Parameter(index: 7, name: "fill_color_hex",
            type: "string",
            description:
                "Optional fill color as hex (\"FF0000\", \"#FF0000\", or 3-digit shorthand \"F00\"). " +
                "If omitted: on update the existing fill is left untouched; on create a default " +
                "theme accent1 fill is used.",
            required: false)]
        [Parameter(index: 8, name: "text",
            type: "string",
            description:
                "Optional plain text for the shape's single-paragraph text body. If omitted: on " +
                "update existing text is left untouched; on create the shape starts with no text.",
            required: false)]
        [Parameter(index: 9, name: "font_color_hex",
            type: "string",
            description:
                "Optional font color as hex (\"FF0000\", \"#FF0000\", or 3-digit shorthand \"F00\") for " +
                "the shape's built-in text set by 'text'. If omitted: on update the existing run color " +
                "is left untouched (theme default); on create the theme's default text color is used.",
            required: false)]
        [Parameter(index: 10, name: "output_path",
            type: "string",
            description:
                "Optional full absolute Windows path to save the modified presentation to. If " +
                "omitted, the original file at 'path' is overwritten in place.",
            required: false)]
        public static object PptxShapeRectangle(JsonObject input)
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

            string? fillColorHex = input["fill_color_hex"]?.GetValue<string>();
            string? text = input["text"]?.GetValue<string>();
            string? fontColorHex = input["font_color_hex"]?.GetValue<string>();
            string? outputPathRaw = input["output_path"]?.GetValue<string>();

            PptxOperationResult result = PptxShapeRectangleTool.SetRectangle(
                bytes, slideNumber.Value, shapeName, top.Value, left.Value, bottom.Value, right.Value,
                fillColorHex, text, fontColorHex: fontColorHex);
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
