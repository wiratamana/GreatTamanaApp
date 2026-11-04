using System.Text.Json.Nodes;
using PptxModifierLib;

namespace GTA.Tools
{
    internal static partial class ToolsPptx
    {
        [ToolTag("pptx_local")]
        [FileMutating]
        [ToolInfo(
            name: "pptx_text_box",
            description:
                "Creates or updates (upserted by 'shape_name') a plain text box (no visible " +
                "fill/outline) on one slide of a LOCAL .pptx file, then SAVES the result back to " +
                "disk. The bounding box is given as its FOUR EDGES — 'top','left','bottom','right' " +
                "— NORMALIZED (0,0)-(1,1) fractions of the slide's actual width/height (see " +
                "pptx_shape_rectangle for the full convention). By default the modified " +
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
            description: "Stable identifier for this text box. Reusing the same name edits it in place.",
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
        [Parameter(index: 7, name: "text",
            type: "string",
            description: "Plain text to set as the text box's single-paragraph text body (always replaces any prior text on update).",
            required: true)]
        [Parameter(index: 8, name: "font_size_pt",
            type: "number",
            description: "Font size in points. Must be positive. Default 18pt. Not normalized.",
            required: false)]
        [Parameter(index: 9, name: "bold",
            type: "boolean",
            description: "Whether the run is bold. Default false.",
            required: false)]
        [Parameter(index: 10, name: "italic",
            type: "boolean",
            description: "Whether the run is italic. Default false.",
            required: false)]
        [Parameter(index: 11, name: "font_color_hex",
            type: "string",
            description: "Optional font color as hex. If omitted, uses the theme's default text color.",
            required: false)]
        [Parameter(index: 12, name: "alignment",
            type: "string",
            description: "Paragraph alignment: \"left\" (default), \"center\", or \"right\".",
            required: false)]
        [Parameter(index: 13, name: "output_path",
            type: "string",
            description: "Optional full absolute Windows path to save to. If omitted, 'path' is overwritten in place.",
            required: false)]
        public static object PptxTextBox(JsonObject input)
        {
            string? err = ToolsPptxHelpers.LoadLocalPptx(input, out byte[] bytes, out string path);
            if (err != null) return err;

            int? slideNumber = ToolsIOHelpers.GetOptionalInt(input["slide_number"]);
            string? shapeName = input["shape_name"]?.GetValue<string>();
            double? top = ToolsIOHelpers.GetOptionalDouble(input["top"]);
            double? left = ToolsIOHelpers.GetOptionalDouble(input["left"]);
            double? bottom = ToolsIOHelpers.GetOptionalDouble(input["bottom"]);
            double? right = ToolsIOHelpers.GetOptionalDouble(input["right"]);
            string? text = input["text"]?.GetValue<string>();

            if (slideNumber == null) return "Error: 'slide_number' parameter is missing.";
            if (string.IsNullOrWhiteSpace(shapeName)) return "Error: 'shape_name' parameter is missing or empty.";
            if (top == null || left == null || bottom == null || right == null)
                return "Error: 'top'/'left'/'bottom'/'right' parameters are required.";
            if (text == null) return "Error: 'text' parameter is missing.";

            double fontSizePt = ToolsIOHelpers.GetOptionalDouble(input["font_size_pt"]) ?? 18;
            bool bold = ToolsIOHelpers.GetOptionalBool(input, "bold", false);
            bool italic = ToolsIOHelpers.GetOptionalBool(input, "italic", false);
            string? fontColorHex = input["font_color_hex"]?.GetValue<string>();
            string alignment = input["alignment"]?.GetValue<string>() ?? "left";
            string? outputPathRaw = input["output_path"]?.GetValue<string>();

            PptxOperationResult result = PptxTextBoxTool.SetTextBox(
                bytes, slideNumber.Value, shapeName, top.Value, left.Value, bottom.Value, right.Value,
                text, fontSizePt, bold, italic, fontColorHex, alignment);
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
