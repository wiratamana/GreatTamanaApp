using System.Text.Json.Nodes;
using PptxModifierLib;

namespace GTA.Tools
{
    internal static partial class ToolsPptx
    {
        [ToolTag("pptx_local")]
        [FileMutating]
        [ToolInfo(
            name: "pptx_new_slide",
            description:
                "Adds one or more new, empty slides to a LOCAL .pptx file, then SAVES the result " +
                "back to disk. By default the modified presentation OVERWRITES the original file " +
                "at 'path' — pass 'output_path' to save as a different file instead. Every new " +
                "slide starts completely empty (no placeholders/shapes) — populate it afterward " +
                "with the pptx_shape_*/pptx_table/pptx_image/pptx_text_box/pptx_bullet_list tools.")]
        [Parameter(index: 0, name: "path",
            type: "string",
            description: "Full absolute Windows path to the local .pptx file to load and modify.",
            required: true)]
        [Parameter(index: 1, name: "layout_name",
            type: "string",
            description:
                "Optional slide-layout display name to attach the new slide(s) to (e.g. \"Title " +
                "Slide\", \"Blank\", case-insensitive). If omitted, prefers a layout whose type is " +
                "literally \"blank\"; if none exists, falls back to the presentation's first " +
                "available slide layout. The SAME layout is used for every new slide when " +
                "slide_count > 1.",
            required: false)]
        [Parameter(index: 2, name: "insert_at_position",
            type: "integer",
            description:
                "Optional 1-based visual slide position to insert at (existing slides at/after " +
                "this position shift right). If omitted, the new slide(s) are appended at the end. " +
                "When slide_count > 1, all new slides are inserted as one contiguous, in-order " +
                "block starting at this position.",
            required: false)]
        [Parameter(index: 3, name: "slide_count",
            type: "integer",
            description:
                "Optional number of new slides to create in this single call (default 1, must be " +
                ">= 1 if given). All slides use the same layout_name/insert_at_position and land " +
                "as one contiguous block, in order.",
            required: false)]
        [Parameter(index: 4, name: "output_path",
            type: "string",
            description:
                "Optional full absolute Windows path to save the modified presentation to. If " +
                "omitted, the original file at 'path' is overwritten in place.",
            required: false)]
        public static object PptxNewSlide(JsonObject input)
        {
            string? err = ToolsPptxHelpers.LoadLocalPptx(input, out byte[] bytes, out string path);
            if (err != null) return err;

            string? layoutName = input["layout_name"]?.GetValue<string>();
            int? insertAtPosition = ToolsIOHelpers.GetOptionalInt(input["insert_at_position"]);
            int slideCount = ToolsIOHelpers.GetOptionalInt(input["slide_count"]) ?? 1;
            string? outputPathRaw = input["output_path"]?.GetValue<string>();

            PptxOperationResult result = PptxNewSlideTool.AddSlide(bytes, layoutName, insertAtPosition, slideCount);
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
