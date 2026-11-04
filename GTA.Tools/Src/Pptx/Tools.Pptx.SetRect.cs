using System.Text.Json.Nodes;
using PptxModifierLib;

namespace GTA.Tools
{
    internal static partial class ToolsPptx
    {
        [ToolTag("pptx_local")]
        [FileMutating]
        [ToolInfo(
            name: "pptx_set_rect",
            description:
                "Moves/resizes an EXISTING object (matched by 'shape_name') on one slide of a LOCAL " +
                ".pptx file to a new bounding box, then SAVES the result back to disk. Works on any " +
                "object a pptx_* tool in this toolset can create: a plain shape (rectangle/rounded " +
                "rectangle/diamond/arrow/text box/bullet list), a picture, a table/chart, or a " +
                "connector/line. UNLIKE the pptx_shape_*/pptx_table/pptx_image/pptx_text_box tools, " +
                "this tool NEVER creates anything — if no object named 'shape_name' exists on " +
                "'slide_number' this call fails with a clear error. Only position/size change; the " +
                "object's own content/fill/text/formatting and any existing rotation are left " +
                "untouched. The new bounding box is given as its FOUR EDGES — 'top','left','bottom'," +
                "'right' — NORMALIZED (0,0)-(1,1) fractions of the slide's actual width/height — (0,0) " +
                "is the slide's top-left corner, (1,1) its bottom-right corner — e.g. " +
                "top=0,left=0,bottom=1,right=1 stretches the object to fill the entire slide. By " +
                "default the modified presentation OVERWRITES the original file at 'path' — pass " +
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
            description: "Display name of the EXISTING object to move/resize. Must already exist on 'slide_number' — this tool never creates anything.",
            required: true)]
        [Parameter(index: 3, name: "top",
            type: "number",
            description: "New top edge, normalized 0.0-1.0 as a fraction of the slide's actual HEIGHT. Must be less than 'bottom'.",
            required: true)]
        [Parameter(index: 4, name: "left",
            type: "number",
            description: "New left edge, normalized 0.0-1.0 as a fraction of the slide's actual WIDTH. Must be less than 'right'.",
            required: true)]
        [Parameter(index: 5, name: "bottom",
            type: "number",
            description: "New bottom edge, normalized 0.0-1.0 as a fraction of the slide's actual HEIGHT. Must be greater than 'top'.",
            required: true)]
        [Parameter(index: 6, name: "right",
            type: "number",
            description: "New right edge, normalized 0.0-1.0 as a fraction of the slide's actual WIDTH. Must be greater than 'left'.",
            required: true)]
        [Parameter(index: 7, name: "output_path",
            type: "string",
            description:
                "Optional full absolute Windows path to save the modified presentation to. If " +
                "omitted, the original file at 'path' is overwritten in place.",
            required: false)]
        public static object PptxSetRect(JsonObject input)
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

            string? outputPathRaw = input["output_path"]?.GetValue<string>();

            PptxOperationResult result = PptxSetRectTool.SetRect(
                bytes, slideNumber.Value, shapeName, top.Value, left.Value, bottom.Value, right.Value);
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
