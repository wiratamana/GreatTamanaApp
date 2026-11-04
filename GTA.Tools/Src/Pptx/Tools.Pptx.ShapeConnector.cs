using System.Text.Json.Nodes;
using PptxModifierLib;

namespace GTA.Tools
{
    internal static partial class ToolsPptx
    {
        [ToolTag("pptx_local")]
        [FileMutating]
        [ToolInfo(
            name: "pptx_shape_connector",
            description:
                "Creates or updates (upserted by 'shape_name') a straight connector GLUED to two " +
                "existing shapes on the same slide of a LOCAL .pptx file — identified by name and a " +
                "connection-site index (0=top, 1=right, 2=bottom, 3=left of that shape's own " +
                "bounding box), NOT literal coordinates — then SAVES the result back to disk. Both " +
                "from_shape_name and to_shape_name must already exist on the target slide (e.g. " +
                "created via pptx_shape_rectangle/pptx_shape_diamond/pptx_shape_rounded_rectangle). " +
                "By default the modified presentation OVERWRITES the original file at 'path' — pass " +
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
            description: "Stable identifier for this connector. Reusing the same name edits it in place.",
            required: true)]
        [Parameter(index: 3, name: "from_shape_name",
            type: "string",
            description: "Name of the existing shape to glue the connector's START to. Must already exist on this slide.",
            required: true)]
        [Parameter(index: 4, name: "from_connection_site_index",
            type: "integer",
            description: "0=top, 1=right, 2=bottom, or 3=left — which edge-center of the FROM shape to glue to.",
            required: true)]
        [Parameter(index: 5, name: "to_shape_name",
            type: "string",
            description: "Name of the existing shape to glue the connector's END to. Must already exist on this slide.",
            required: true)]
        [Parameter(index: 6, name: "to_connection_site_index",
            type: "integer",
            description: "0=top, 1=right, 2=bottom, or 3=left — which edge-center of the TO shape to glue to.",
            required: true)]
        [Parameter(index: 7, name: "has_arrow",
            type: "boolean",
            description: "If true (default), the end point carries an arrowhead. If false, a plain line with no arrowhead.",
            required: false)]
        [Parameter(index: 8, name: "line_width_pt",
            type: "number",
            description: "Line width, in points. Must be positive. Default 1.5pt.",
            required: false)]
        [Parameter(index: 9, name: "line_color_hex",
            type: "string",
            description: "Optional line color as hex. If omitted, uses the theme text/outline color default.",
            required: false)]
        [Parameter(index: 10, name: "arrow_style",
            type: "string",
            description: "\"triangle\" (default), \"stealth\", \"oval\", \"diamond\", or \"arrow\" — only used when has_arrow is true.",
            required: false)]
        [Parameter(index: 11, name: "line_dash_style",
            type: "string",
            description: "\"solid\" (default), \"dash\", \"dashDot\", \"sysDot\", \"sysDash\", \"lgDash\", \"lgDashDot\", or \"lgDashDotDot\".",
            required: false)]
        [Parameter(index: 12, name: "output_path",
            type: "string",
            description: "Optional full absolute Windows path to save to. If omitted, 'path' is overwritten in place.",
            required: false)]
        public static object PptxShapeConnector(JsonObject input)
        {
            string? err = ToolsPptxHelpers.LoadLocalPptx(input, out byte[] bytes, out string path);
            if (err != null) return err;

            int? slideNumber = ToolsIOHelpers.GetOptionalInt(input["slide_number"]);
            string? shapeName = input["shape_name"]?.GetValue<string>();
            string? fromShapeName = input["from_shape_name"]?.GetValue<string>();
            int? fromSiteIndex = ToolsIOHelpers.GetOptionalInt(input["from_connection_site_index"]);
            string? toShapeName = input["to_shape_name"]?.GetValue<string>();
            int? toSiteIndex = ToolsIOHelpers.GetOptionalInt(input["to_connection_site_index"]);

            if (slideNumber == null) return "Error: 'slide_number' parameter is missing.";
            if (string.IsNullOrWhiteSpace(shapeName)) return "Error: 'shape_name' parameter is missing or empty.";
            if (string.IsNullOrWhiteSpace(fromShapeName)) return "Error: 'from_shape_name' parameter is missing or empty.";
            if (fromSiteIndex == null) return "Error: 'from_connection_site_index' parameter is missing.";
            if (string.IsNullOrWhiteSpace(toShapeName)) return "Error: 'to_shape_name' parameter is missing or empty.";
            if (toSiteIndex == null) return "Error: 'to_connection_site_index' parameter is missing.";

            bool hasArrow = ToolsIOHelpers.GetOptionalBool(input, "has_arrow", true);
            double lineWidthPt = ToolsIOHelpers.GetOptionalDouble(input["line_width_pt"]) ?? 1.5;
            string? lineColorHex = input["line_color_hex"]?.GetValue<string>();
            string arrowStyle = input["arrow_style"]?.GetValue<string>() ?? "triangle";
            string lineDashStyle = input["line_dash_style"]?.GetValue<string>() ?? "solid";
            string? outputPathRaw = input["output_path"]?.GetValue<string>();

            PptxOperationResult result = PptxShapeConnectorTool.SetShapeConnector(
                bytes, slideNumber.Value, shapeName, fromShapeName, fromSiteIndex.Value,
                toShapeName, toSiteIndex.Value, hasArrow, lineWidthPt, lineColorHex, arrowStyle, lineDashStyle);
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
