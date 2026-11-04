using System;
using System.Text.Json.Nodes;
using PptxModifierLib;

namespace GTA.Tools
{
    internal static partial class ToolsPptx
    {
        [ToolTag("pptx_local")]
        [FileMutating]
        [ToolInfo(
            name: "pptx_chart",
            description:
                "Creates or fully replaces (matched by 'shape_name') a native chart shape (line, " +
                "bar, or column) on one slide of a LOCAL .pptx file, then SAVES the result back to " +
                "disk. Repeat calls with the SAME shape_name replace the ENTIRE chart (categories, " +
                "series, axes, legend, title) — there is no partial data-point edit. The bounding " +
                "box is given as its FOUR EDGES — 'top','left','bottom','right' — NORMALIZED " +
                "(0,0)-(1,1) fractions of the slide's actual width/height (see pptx_shape_rectangle " +
                "for the full convention). The chart's visible data is written as CACHED values " +
                "only — there is no embedded 'Edit Data in Excel' workbook. By default the modified " +
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
            description: "Stable identifier for this chart. Reusing the same name fully replaces it in place.",
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
        [Parameter(index: 7, name: "chart_type",
            type: "string",
            description: "\"line\", \"bar\" (horizontal bars), or \"column\" (vertical bars).",
            required: true)]
        [Parameter(index: 8, name: "categories",
            type: "string",
            description:
                "JSON array of category labels along the category axis, e.g. " +
                "[\"Cycle 1\",\"Cycle 2\",\"Cycle 3\"]. Must contain at least one entry; every " +
                "series' values array must have EXACTLY this many entries, in the same order.",
            required: true)]
        [Parameter(index: 9, name: "series",
            type: "string",
            description:
                "JSON array of [name, values] pairs — one line/bar-group per entry, each plotted " +
                "against the SAME 'categories', e.g. " +
                "[[\"Revenue\",[10,20,30]],[\"Cost\",[5,8,12]]]. Must contain at least one series; " +
                "every values array's length must equal categories.length exactly.",
            required: true)]
        [Parameter(index: 10, name: "title",
            type: "string",
            description: "Optional chart title text. Omitted/empty = no title shown.",
            required: false)]
        [Parameter(index: 11, name: "show_legend",
            type: "boolean",
            description: "If true (default), a legend is shown below the plot area naming each series.",
            required: false)]
        [Parameter(index: 12, name: "output_path",
            type: "string",
            description: "Optional full absolute Windows path to save to. If omitted, 'path' is overwritten in place.",
            required: false)]
        public static object PptxChart(JsonObject input)
        {
            string? err = ToolsPptxHelpers.LoadLocalPptx(input, out byte[] bytes, out string path);
            if (err != null) return err;

            int? slideNumber = ToolsIOHelpers.GetOptionalInt(input["slide_number"]);
            string? shapeName = input["shape_name"]?.GetValue<string>();
            double? top = ToolsIOHelpers.GetOptionalDouble(input["top"]);
            double? left = ToolsIOHelpers.GetOptionalDouble(input["left"]);
            double? bottom = ToolsIOHelpers.GetOptionalDouble(input["bottom"]);
            double? right = ToolsIOHelpers.GetOptionalDouble(input["right"]);
            string? chartType = input["chart_type"]?.GetValue<string>();
            string? categoriesJson = input["categories"]?.GetValue<string>();
            string? seriesJson = input["series"]?.GetValue<string>();

            if (slideNumber == null) return "Error: 'slide_number' parameter is missing.";
            if (string.IsNullOrWhiteSpace(shapeName)) return "Error: 'shape_name' parameter is missing or empty.";
            if (top == null || left == null || bottom == null || right == null)
                return "Error: 'top'/'left'/'bottom'/'right' parameters are required.";
            if (string.IsNullOrWhiteSpace(chartType)) return "Error: 'chart_type' parameter is missing or empty.";
            if (string.IsNullOrWhiteSpace(categoriesJson))
                return "Error: 'categories' parameter is missing or empty. Expected a JSON array like " +
                       "[\"Cycle 1\",\"Cycle 2\"].";
            if (string.IsNullOrWhiteSpace(seriesJson))
                return "Error: 'series' parameter is missing or empty. Expected a JSON array of " +
                       "[name, values] pairs like [[\"Revenue\",[10,20,30]]].";

            string[] categories;
            (string Name, double[] Values)[] series;
            try
            {
                categories = ToolsPptxHelpers.ParseChartCategories(categoriesJson);
            }
            catch (Exception ex)
            {
                return $"❌ pptx_chart — invalid 'categories' JSON: {ex.Message}";
            }
            try
            {
                series = ToolsPptxHelpers.ParseChartSeries(seriesJson);
            }
            catch (Exception ex)
            {
                return $"❌ pptx_chart — invalid 'series' JSON: {ex.Message}";
            }

            string? title = input["title"]?.GetValue<string>();
            bool showLegend = ToolsIOHelpers.GetOptionalBool(input, "show_legend", true);
            string? outputPathRaw = input["output_path"]?.GetValue<string>();

            PptxOperationResult result = PptxChartTool.SetChart(
                bytes, slideNumber.Value, shapeName, top.Value, left.Value, bottom.Value, right.Value,
                chartType!, categories, series, title, showLegend);
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
