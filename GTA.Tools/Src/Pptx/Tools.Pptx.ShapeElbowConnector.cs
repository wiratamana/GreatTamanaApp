using System;
using System.Text.Json.Nodes;
using PptxModifierLib;

namespace GTA.Tools
{
    internal static partial class ToolsPptx
    {
        [FileMutating]
        [ToolInfo(
            name: "pptx_shape_elbow_connector",
            description:
                "Creates or updates (upserted by 'shape_name') a multi-point path connector between two " +
                "literal points on one slide of a LOCAL .pptx file, then SAVES the result back to " +
                "disk. TWO MODES, selected by whether 'checkpoints' is supplied: (1) LITERAL MODE — " +
                "pass 'checkpoints' (a JSON array of [x,y] waypoints) and the path is built as EXACTLY " +
                "start -> checkpoints[0] -> checkpoints[1] -> ... -> end, straight segments through each " +
                "waypoint in order — this is the general-purpose way to draw an arbitrary bent/zig-zag " +
                "line through as many bend points as you like (e.g. an elbow with several turns). " +
                "(2) AUTO LOOP MODE (default, when 'checkpoints' is omitted) — the ORIGINAL behavior: a " +
                "fixed auto-computed loop that visibly loops OUTSIDE the straight bounding box between " +
                "its endpoints: it exits start_x/start_y outward (away from the end point), drops to a " +
                "clearance line below both points, sweeps across past the end point, rises to its level, " +
                "then turns in from its far side — the standard way to route a 'return'/'loop-back' flow " +
                "arrow. start_x/start_y/end_x/end_y/loop_clearance/checkpoints are all NORMALIZED " +
                "(0,0)-(1,1) fractions of the slide's actual width/height (see pptx_shape_rectangle for " +
                "the full convention). By default the modified presentation OVERWRITES the original file " +
                "at 'path' — pass 'output_path' to save as a different file instead.")]
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
        [Parameter(index: 3, name: "start_x",
            type: "number",
            description: "Start point X, normalized 0.0-1.0 as a fraction of the slide's actual WIDTH. The path first exits AWAY from the end point before dropping down.",
            required: true)]
        [Parameter(index: 4, name: "start_y",
            type: "number",
            description: "Start point Y, normalized 0.0-1.0 as a fraction of the slide's actual HEIGHT.",
            required: true)]
        [Parameter(index: 5, name: "end_x",
            type: "number",
            description: "End point X, normalized as a fraction of the slide's WIDTH. The path approaches this point from its far side.",
            required: true)]
        [Parameter(index: 6, name: "end_y",
            type: "number",
            description: "End point Y, normalized as a fraction of the slide's HEIGHT.",
            required: true)]
        [Parameter(index: 7, name: "has_arrow",
            type: "boolean",
            description: "If true (default), the end point carries an arrowhead. If false, a plain line with no arrowhead.",
            required: false)]
        [Parameter(index: 8, name: "line_width_pt",
            type: "number",
            description: "Line width, in points. Must be positive. Default 1.5pt. Not normalized.",
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
        [Parameter(index: 12, name: "loop_clearance",
            type: "number",
            description:
                "How far the loop extends beyond each point, normalized as a fraction of the " +
                "slide's actual HEIGHT: outward at start/end, and below the lower of the two points " +
                "at the bottom of the loop. Must be positive. If omitted, defaults to ~0.0667 " +
                "(roughly 0.5in on a standard 7.5in-tall slide).",
            required: false)]
        [Parameter(index: 13, name: "checkpoints",
            type: "string",
            description:
                "OPTIONAL JSON array of [x,y] normalized-coordinate waypoint pairs, e.g. " +
                "\"[[0.3,0.2],[0.5,0.45],[0.62,0.3]]\". When supplied (non-empty), SWITCHES this tool " +
                "into LITERAL MODE: the connector is built as EXACTLY start -> checkpoints[0] -> " +
                "checkpoints[1] -> ... -> end (straight segments through each waypoint, in the exact " +
                "order given — nothing is auto-computed or reordered), and 'loop_clearance' is ignored. " +
                "When omitted (default), the tool falls back to its original AUTO LOOP MODE behavior.",
            required: false)]
        [Parameter(index: 14, name: "output_path",
            type: "string",
            description: "Optional full absolute Windows path to save to. If omitted, 'path' is overwritten in place.",
            required: false)]
        public static object PptxShapeElbowConnector(JsonObject input)
        {
            string? err = ToolsPptxHelpers.LoadLocalPptx(input, out byte[] bytes, out string path);
            if (err != null) return err;

            int? slideNumber = ToolsIOHelpers.GetOptionalInt(input["slide_number"]);
            string? shapeName = input["shape_name"]?.GetValue<string>();
            double? startX = ToolsIOHelpers.GetOptionalDouble(input["start_x"]);
            double? startY = ToolsIOHelpers.GetOptionalDouble(input["start_y"]);
            double? endX = ToolsIOHelpers.GetOptionalDouble(input["end_x"]);
            double? endY = ToolsIOHelpers.GetOptionalDouble(input["end_y"]);

            if (slideNumber == null) return "Error: 'slide_number' parameter is missing.";
            if (string.IsNullOrWhiteSpace(shapeName)) return "Error: 'shape_name' parameter is missing or empty.";
            if (startX == null || startY == null || endX == null || endY == null)
                return "Error: 'start_x'/'start_y'/'end_x'/'end_y' parameters are required.";

            bool hasArrow = ToolsIOHelpers.GetOptionalBool(input, "has_arrow", true);
            double lineWidthPt = ToolsIOHelpers.GetOptionalDouble(input["line_width_pt"]) ?? 1.5;
            string? lineColorHex = input["line_color_hex"]?.GetValue<string>();
            string arrowStyle = input["arrow_style"]?.GetValue<string>() ?? "triangle";
            string lineDashStyle = input["line_dash_style"]?.GetValue<string>() ?? "solid";
            double? loopClearance = ToolsIOHelpers.GetOptionalDouble(input["loop_clearance"]);
            string? outputPathRaw = input["output_path"]?.GetValue<string>();

            (double X, double Y)[]? checkpoints = null;
            string? checkpointsJson = input["checkpoints"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(checkpointsJson))
            {
                try { checkpoints = ToolsPptxHelpers.ParseCheckpoints(checkpointsJson); }
                catch (Exception ex) { return $"Error: 'checkpoints' — invalid JSON: {ex.Message}"; }
            }

            PptxOperationResult result = PptxShapeElbowConnectorTool.SetShapeElbowConnector(
                bytes, slideNumber.Value, shapeName, startX.Value, startY.Value, endX.Value, endY.Value,
                hasArrow, lineWidthPt, lineColorHex, arrowStyle, lineDashStyle, loopClearance, checkpoints);
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
