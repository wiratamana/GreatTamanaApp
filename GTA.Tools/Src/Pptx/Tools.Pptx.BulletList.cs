using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using PptxModifierLib;

namespace GTA.Tools
{
    internal static partial class ToolsPptx
    {
        [ToolTag("pptx_local")]
        [FileMutating]
        [ToolInfo(
            name: "pptx_bullet_list",
            description:
                "Creates or updates (upserted by 'shape_name') a multi-level bulleted OR numbered " +
                "list text box on one slide of a LOCAL .pptx file, then SAVES the result back to " +
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
            description: "Stable identifier for this list. Reusing the same name edits it in place (replacing its entire item list).",
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
        [Parameter(index: 7, name: "items",
            type: "string",
            description:
                "JSON array of items — one per bullet/numbered line — where each entry is either a " +
                "plain string (indent level 0) or a [text, indentLevel] pair (indentLevel 0-8, 0 = " +
                "top level), e.g. [\"Top-level point\",[\"Sub point\",1],[\"Sub-sub point\",2]]. " +
                "Must contain at least one item.",
            required: true)]
        [Parameter(index: 8, name: "numbered",
            type: "boolean",
            description:
                "If true, every paragraph gets an auto-numbered \"1.  2.  3. ...\" bullet instead of " +
                "a bullet character. Default false (bulleted).",
            required: false)]
        [Parameter(index: 9, name: "output_path",
            type: "string",
            description: "Optional full absolute Windows path to save to. If omitted, 'path' is overwritten in place.",
            required: false)]
        public static object PptxBulletList(JsonObject input)
        {
            string? err = ToolsPptxHelpers.LoadLocalPptx(input, out byte[] bytes, out string path);
            if (err != null) return err;

            int? slideNumber = ToolsIOHelpers.GetOptionalInt(input["slide_number"]);
            string? shapeName = input["shape_name"]?.GetValue<string>();
            double? top = ToolsIOHelpers.GetOptionalDouble(input["top"]);
            double? left = ToolsIOHelpers.GetOptionalDouble(input["left"]);
            double? bottom = ToolsIOHelpers.GetOptionalDouble(input["bottom"]);
            double? right = ToolsIOHelpers.GetOptionalDouble(input["right"]);
            string? itemsJson = input["items"]?.GetValue<string>();

            if (slideNumber == null) return "Error: 'slide_number' parameter is missing.";
            if (string.IsNullOrWhiteSpace(shapeName)) return "Error: 'shape_name' parameter is missing or empty.";
            if (top == null || left == null || bottom == null || right == null)
                return "Error: 'top'/'left'/'bottom'/'right' parameters are required.";
            if (string.IsNullOrWhiteSpace(itemsJson))
                return "Error: 'items' parameter is missing or empty. Expected a JSON array like " +
                       "[\"Top-level point\",[\"Sub point\",1]].";

            List<(string Text, int IndentLevel)> items;
            try
            {
                items = ToolsPptxHelpers.ParseBulletItems(itemsJson);
            }
            catch (Exception ex)
            {
                return $"❌ pptx_bullet_list — invalid 'items' JSON: {ex.Message}";
            }
            if (items.Count == 0)
                return "Error: 'items' parsed to zero entries.";

            bool numbered = ToolsIOHelpers.GetOptionalBool(input, "numbered", false);
            string? outputPathRaw = input["output_path"]?.GetValue<string>();

            PptxOperationResult result = PptxBulletListTool.SetBulletList(
                bytes, slideNumber.Value, shapeName, top.Value, left.Value, bottom.Value, right.Value,
                items, numbered);
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
