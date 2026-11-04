using System;
using System.IO;
using System.Text.Json.Nodes;
using PptxModifierLib;

namespace GTA.Tools
{
    internal static partial class ToolsPptx
    {
        [ToolTag("pptx_local")]
        [FileMutating]
        [ToolInfo(
            name: "pptx_image",
            description:
                "Embeds a picture (upserted by 'shape_name') into one slide of a LOCAL .pptx file, " +
                "using the raw bytes of a SECOND local image file at 'image_path' (PNG or JPEG), " +
                "then SAVES the result back to disk. The bounding box is given as its FOUR EDGES — " +
                "'top','left','bottom','right' — NORMALIZED (0,0)-(1,1) fractions of the slide's " +
                "actual width/height (see pptx_shape_rectangle for the full convention). By default " +
                "the modified presentation OVERWRITES the original file at 'path' — pass " +
                "'output_path' to save as a different file instead.")]
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
            description: "Stable identifier for this picture. Reusing the same name replaces its image/position/size in place.",
            required: true)]
        [Parameter(index: 3, name: "image_path",
            type: "string",
            description: "Full absolute Windows path to the local PNG or JPEG image file to embed.",
            required: true)]
        [Parameter(index: 4, name: "top",
            type: "number",
            description: "Top edge, normalized 0.0-1.0 as a fraction of the slide's actual HEIGHT. Must be less than 'bottom'.",
            required: true)]
        [Parameter(index: 5, name: "left",
            type: "number",
            description: "Left edge, normalized 0.0-1.0 as a fraction of the slide's actual WIDTH. Must be less than 'right'.",
            required: true)]
        [Parameter(index: 6, name: "bottom",
            type: "number",
            description: "Bottom edge, normalized 0.0-1.0 as a fraction of the slide's actual HEIGHT. Must be greater than 'top'.",
            required: true)]
        [Parameter(index: 7, name: "right",
            type: "number",
            description: "Right edge, normalized 0.0-1.0 as a fraction of the slide's actual WIDTH. Must be greater than 'left'.",
            required: true)]
        [Parameter(index: 8, name: "image_format",
            type: "string",
            description: "\"png\" or \"jpeg\"/\"jpg\". If omitted, auto-detected from image_path's file extension.",
            required: false)]
        [Parameter(index: 9, name: "output_path",
            type: "string",
            description: "Optional full absolute Windows path to save to. If omitted, 'path' is overwritten in place.",
            required: false)]
        public static object PptxImage(JsonObject input)
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

            string? imageErr = ToolsIOHelpers.ResolveExistingFile(input, "image_path", out string imagePath);
            if (imageErr != null) return imageErr;

            byte[] imageBytes;
            try
            {
                imageBytes = File.ReadAllBytes(imagePath);
            }
            catch (Exception ex)
            {
                return $"Error: Unexpected error while reading '{imagePath}': {ex.Message}";
            }

            string? imageFormat = input["image_format"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(imageFormat))
            {
                string ext = Path.GetExtension(imagePath).TrimStart('.').ToLowerInvariant();
                imageFormat = ext switch
                {
                    "png" => "png",
                    "jpg" or "jpeg" => "jpeg",
                    _ => ext
                };
            }

            string? outputPathRaw = input["output_path"]?.GetValue<string>();

            PptxOperationResult result = PptxImageTool.SetImage(
                bytes, slideNumber.Value, shapeName, imageBytes, imageFormat, top.Value, left.Value,
                bottom.Value, right.Value);
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
