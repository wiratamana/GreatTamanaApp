using System.Text.Json.Nodes;
using PptxModifierLib;

namespace GTA.Tools
{
    internal static partial class ToolsPptx
    {
        [ToolTag("pptx_local")]
        [FileMutating]
        [ToolInfo(
            name: "pptx_delete_slide",
            description:
                "Permanently deletes one existing slide (by its current 1-based visual position) " +
                "from a LOCAL .pptx file, then SAVES the result back to disk. Refuses to delete the " +
                "only remaining slide — a presentation must keep at least one slide. By default the " +
                "modified presentation OVERWRITES the original file at 'path' — pass 'output_path' " +
                "to save as a different file instead.")]
        [Parameter(index: 0, name: "path",
            type: "string",
            description: "Full absolute Windows path to the local .pptx file to load and modify.",
            required: true)]
        [Parameter(index: 1, name: "position",
            type: "integer",
            description: "1-based visual position of the slide to delete.",
            required: true)]
        [Parameter(index: 2, name: "output_path",
            type: "string",
            description:
                "Optional full absolute Windows path to save the modified presentation to. If " +
                "omitted, the original file at 'path' is overwritten in place.",
            required: false)]
        public static object PptxDeleteSlide(JsonObject input)
        {
            string? err = ToolsPptxHelpers.LoadLocalPptx(input, out byte[] bytes, out string path);
            if (err != null) return err;

            int? position = ToolsIOHelpers.GetOptionalInt(input["position"]);
            if (position == null) return "Error: 'position' parameter is missing.";

            string? outputPathRaw = input["output_path"]?.GetValue<string>();

            PptxOperationResult result = PptxDeleteSlideTool.DeleteSlide(bytes, position.Value);
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
