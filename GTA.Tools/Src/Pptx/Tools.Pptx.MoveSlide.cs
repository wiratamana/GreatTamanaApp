using System.Text.Json.Nodes;
using PptxModifierLib;

namespace GTA.Tools
{
    internal static partial class ToolsPptx
    {
        [ToolTag("pptx_local")]
        [FileMutating]
        [ToolInfo(
            name: "pptx_move_slide",
            description:
                "Moves one existing slide of a LOCAL .pptx file from its current 1-based position " +
                "to a new 1-based position (the slides in between shift accordingly), then SAVES " +
                "the result back to disk. By default the modified presentation OVERWRITES the " +
                "original file at 'path' — pass 'output_path' to save as a different file instead.")]
        [Parameter(index: 0, name: "path",
            type: "string",
            description: "Full absolute Windows path to the local .pptx file to load and modify.",
            required: true)]
        [Parameter(index: 1, name: "from_position",
            type: "integer",
            description: "1-based CURRENT position of the slide to move.",
            required: true)]
        [Parameter(index: 2, name: "to_position",
            type: "integer",
            description: "1-based position the slide should occupy after the move.",
            required: true)]
        [Parameter(index: 3, name: "output_path",
            type: "string",
            description:
                "Optional full absolute Windows path to save the modified presentation to. If " +
                "omitted, the original file at 'path' is overwritten in place.",
            required: false)]
        public static object PptxMoveSlide(JsonObject input)
        {
            string? err = ToolsPptxHelpers.LoadLocalPptx(input, out byte[] bytes, out string path);
            if (err != null) return err;

            int? fromPosition = ToolsIOHelpers.GetOptionalInt(input["from_position"]);
            int? toPosition = ToolsIOHelpers.GetOptionalInt(input["to_position"]);
            if (fromPosition == null) return "Error: 'from_position' parameter is missing.";
            if (toPosition == null) return "Error: 'to_position' parameter is missing.";

            string? outputPathRaw = input["output_path"]?.GetValue<string>();

            PptxOperationResult result = PptxMoveSlideTool.MoveSlide(bytes, fromPosition.Value, toPosition.Value);
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
