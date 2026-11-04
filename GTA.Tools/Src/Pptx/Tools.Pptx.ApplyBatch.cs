using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using GTA.PptxLib;
using PptxModifierLib;

namespace GTA.Tools
{
    internal static partial class ToolsPptx
    {
        [ToolTag("pptx_local")]
        [FileMutating]
        [ToolInfo(
            name: "pptx_apply_batch",
            description:
                "Applies MANY shape/content operations to ONE slide of a LOCAL .pptx file in a " +
                "SINGLE load->modify->save round trip (the file is read from disk ONCE, every " +
                "operation is applied in array order against the same in-memory copy, then the " +
                "result is SAVED back to disk ONCE) instead of one round trip per shape. Each entry " +
                "in 'operations' is a JSON object with an 'op' field naming which existing pptx_* " +
                "tool behavior to run, plus that tool's own normal parameters using the SAME field " +
                "names as the standalone tool (e.g. 'shape_name','top','left','bottom','right'," +
                "'text', ...) — see each standalone tool's own docs for full parameter semantics. " +
                "UNLIKE the standalone tools, array/table-shaped parameters (an op's 'items'/'rows'/" +
                "'column_widths'/'categories'/'series') may be given here as EITHER a real native " +
                "JSON array (natural, since this whole call is already one JSON document) OR a " +
                "pre-encoded JSON string (identical to how the standalone tool requires it) — both " +
                "forms work. Supported 'op' values: text_box, bullet_list, table, image, chart, " +
                "rectangle, rounded_rectangle, diamond, arrow, connector, elbow_connector, line, " +
                "line_arrow. Operations run in array order against the SAME in-memory copy of the " +
                "file, so e.g. a connector at index 3 can reference a shape_name a rectangle at " +
                "index 0 just created earlier in the SAME batch. Every operation targets " +
                "'slide_number' by default, unless one particular operation object itself includes " +
                "its own 'slide_number' override. By default (stop_on_error=true, the safer choice) " +
                "a failing operation ABORTS the ENTIRE batch and NOTHING is saved — exactly as if " +
                "the whole call had never happened; pass stop_on_error=false to instead apply every " +
                "operation that succeeds, skip the ones that fail, and still save the partial " +
                "result. Supply the operations list EITHER inline via 'operations' OR from a file " +
                "via 'spec_path' (not both) — drafting a spec file first with write_file lets it be " +
                "reviewed as plain text and reused as a reusable template (e.g. swap just the " +
                "'text' fields to re-apply the same layout to a new topic/file). By default the " +
                "modified presentation OVERWRITES the original file at 'path' — pass 'output_path' " +
                "to save as a different file instead. PREVIEW: after a successful save, this tool " +
                "(by default, render_preview=true) also renders the target slide_number to a PNG " +
                "image (via PowerPoint COM, same mechanism as pptx_read) and returns it as an image " +
                "content block right in this response, so you can see the actual result immediately " +
                "without a separate pptx_read call — pass render_preview=false to skip this and get " +
                "only the text report back (faster, no PowerPoint COM round trip).")]
        [Parameter(index: 0, name: "path",
            type: "string",
            description: "Full absolute Windows path to the local .pptx file to load and modify.",
            required: true)]
        [Parameter(index: 1, name: "slide_number",
            type: "integer",
            description: "1-based slide position (in visual/presentation order). Default target for every operation unless it has its own 'slide_number' override. Also the slide rendered for the post-batch preview image.",
            required: true)]
        [Parameter(index: 2, name: "operations",
            type: "string",
            description:
                "JSON array of operation objects, each shaped { \"op\": \"<op name>\", ...that op's " +
                "own parameters using the same field names as the matching standalone pptx_* tool }. " +
                "Required unless 'spec_path' is supplied instead. Must contain at least one " +
                "operation. Example: " +
                "[{\"op\":\"rectangle\",\"shape_name\":\"Box1\",\"top\":0.1,\"left\":0.1,\"bottom\":0.2,\"right\":0.3,\"fill_color_hex\":\"4472C4\",\"text\":\"Start\"}," +
                "{\"op\":\"connector\",\"shape_name\":\"c1\",\"from_shape_name\":\"Box1\",\"from_connection_site_index\":1,\"to_shape_name\":\"Box2\",\"to_connection_site_index\":3}]",
            required: false)]
        [Parameter(index: 3, name: "spec_path",
            type: "string",
            description: "Optional full absolute Windows path to a JSON file containing the same operations array shape as 'operations', as an alternative to inlining it. Supply only ONE of 'operations'/'spec_path'.",
            required: false)]
        [Parameter(index: 4, name: "stop_on_error",
            type: "boolean",
            description: "If true (default), the FIRST failing operation aborts the whole batch and nothing is saved (atomic). If false, failing operations are skipped/reported and every successful operation is still saved.",
            required: false)]
        [Parameter(index: 5, name: "output_path",
            type: "string",
            description: "Optional full absolute Windows path to save to. If omitted, 'path' is overwritten in place.",
            required: false)]
        [Parameter(index: 6, name: "render_preview",
            type: "boolean",
            description: "If true (default), after a successful save this tool renders 'slide_number' to PNG (via PowerPoint COM, same mechanism as pptx_read) and returns it as an image content block in this same response. Pass false to skip rendering and get only the text report (faster).",
            required: false)]
        [Parameter(index: 7, name: "preview_width",
            type: "integer",
            description: "PNG preview export width in pixels. Default: 1920. Ignored if render_preview=false.",
            required: false)]
        [Parameter(index: 8, name: "preview_height",
            type: "integer",
            description: "PNG preview export height in pixels. Default: 1080. Ignored if render_preview=false.",
            required: false)]
        [Parameter(index: 9, name: "preview_timeout_ms",
            type: "integer",
            description: "Preview rendering timeout in milliseconds. Floored to 180000 (3 minutes) internally. Default: 180000. Ignored if render_preview=false.",
            required: false)]
        public static object PptxApplyBatch(JsonObject input)
        {
            string? err = ToolsPptxHelpers.LoadLocalPptx(input, out byte[] bytes, out string path);
            if (err != null) return err;

            int? slideNumber = ToolsIOHelpers.GetOptionalInt(input["slide_number"]);
            if (slideNumber == null) return "Error: 'slide_number' parameter is missing.";

            string? operationsJson = input["operations"]?.GetValue<string>();
            string? specPathRaw = input["spec_path"]?.GetValue<string>();

            if (string.IsNullOrWhiteSpace(operationsJson) && string.IsNullOrWhiteSpace(specPathRaw))
                return "Error: supply either 'operations' (inline JSON array) or 'spec_path' (path to a JSON file with the same shape).";
            if (!string.IsNullOrWhiteSpace(operationsJson) && !string.IsNullOrWhiteSpace(specPathRaw))
                return "Error: supply only ONE of 'operations' or 'spec_path', not both.";

            string operationsText;
            if (!string.IsNullOrWhiteSpace(specPathRaw))
            {
                string? specErr = ToolsIOHelpers.ResolveExistingFile(input, "spec_path", out string specPath);
                if (specErr != null) return specErr;
                try
                {
                    operationsText = File.ReadAllText(specPath);
                }
                catch (Exception ex)
                {
                    return $"Error: Unexpected error while reading '{specPath}': {ex.Message}";
                }
            }
            else
            {
                operationsText = operationsJson!;
            }

            JsonArray opsArray;
            try
            {
                JsonNode? root = JsonNode.Parse(operationsText);
                if (root is not JsonArray arr)
                    return "Error: 'operations'/'spec_path' JSON must be a JSON array of operation objects, e.g. [{\"op\":\"rectangle\",...}].";
                opsArray = arr;
            }
            catch (Exception ex)
            {
                return $"❌ pptx_apply_batch — invalid operations JSON: {ex.Message}";
            }

            if (opsArray.Count == 0)
                return "Error: operations array must contain at least one operation.";

            bool stopOnError = ToolsIOHelpers.GetOptionalBool(input, "stop_on_error", true);
            string? outputPathRaw = input["output_path"]?.GetValue<string>();

            var report = new StringBuilder();
            report.AppendLine("--- pptx_apply_batch ---");
            report.AppendLine($"Applying {opsArray.Count} operation(s) to slide {slideNumber.Value} of '{path}'...");

            int succeeded = 0, failed = 0;
            bool aborted = false;

            for (int i = 0; i < opsArray.Count; i++)
            {
                if (opsArray[i] is not JsonObject opObj)
                {
                    failed++;
                    report.AppendLine($"[{i}] ❌ operation must be a JSON object.");
                    if (stopOnError) { aborted = true; break; }
                    continue;
                }

                string? opName = opObj["op"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(opName))
                {
                    failed++;
                    report.AppendLine($"[{i}] ❌ missing 'op' field naming which operation to run.");
                    if (stopOnError) { aborted = true; break; }
                    continue;
                }

                int effectiveSlide = ToolsIOHelpers.GetOptionalInt(opObj["slide_number"]) ?? slideNumber.Value;
                string? shapeNameForLabel = opObj["shape_name"]?.GetValue<string>();
                string opLabel = $"[{i}] {opName}" + (string.IsNullOrEmpty(shapeNameForLabel) ? "" : $" \"{shapeNameForLabel}\"");

                PptxOperationResult opResult;
                try
                {
                    opResult = Dispatch(opName, bytes, effectiveSlide, opObj);
                }
                catch (Exception ex)
                {
                    opResult = new PptxOperationResult { Success = false, ModifiedBytes = null, ReportText = $"❌ Unexpected error: {ex.Message}" };
                }

                if (opResult.Success && opResult.ModifiedBytes != null)
                {
                    bytes = opResult.ModifiedBytes;
                    succeeded++;
                    report.AppendLine($"{opLabel}: {StripHeader(opResult.ReportText)}");
                }
                else
                {
                    failed++;
                    report.AppendLine($"{opLabel}: {StripHeader(opResult.ReportText)}");
                    if (stopOnError) { aborted = true; break; }
                }
            }

            report.AppendLine();

            if (aborted)
            {
                report.AppendLine(
                    $"❌ Batch ABORTED at the operation above ({succeeded} operation(s) had already " +
                    "succeeded in-memory, but stop_on_error=true means NOTHING was saved — the file " +
                    "on disk is untouched). Fix the failing operation and retry the whole batch, or " +
                    "pass stop_on_error=false to save every operation that succeeds.");
                return report.ToString().TrimEnd();
            }

            report.AppendLine($"Batch complete: {succeeded} succeeded, {failed} failed.");

            if (succeeded == 0)
            {
                report.AppendLine("Nothing changed — no file was written.");
                return report.ToString().TrimEnd();
            }

            (string? saveErr, string savedPath) = ToolsPptxHelpers.SaveLocalPptx(bytes, path, outputPathRaw);

            if (saveErr != null)
                return $"{report}\n\n{saveErr}";

            string finalReport = $"{report}\n\n💾 Saved to: {savedPath}";

            bool renderPreview = ToolsIOHelpers.GetOptionalBool(input, "render_preview", true);
            if (!renderPreview)
                return finalReport;

            return BuildPreviewResponse(bytes, slideNumber.Value, finalReport, input);
        }

        private static object BuildPreviewResponse(byte[] finalBytes, int slideNumber, string textReport, JsonObject input)
        {
            int width = ToolsIOHelpers.GetOptionalInt(input["preview_width"]) ?? 1920;
            int height = ToolsIOHelpers.GetOptionalInt(input["preview_height"]) ?? 1080;
            int timeoutMs = ToolsIOHelpers.GetOptionalInt(input["preview_timeout_ms"]) ?? 180_000;

            var content = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = textReport }
            };

            PptxReadResult readResult;
            try
            {
                readResult = PptxReadTool.ReadSlides(finalBytes, width, height, timeoutMs);
            }
            catch (Exception ex)
            {
                content.Add(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = $"⚠️ Slide preview unavailable — unexpected error while rendering: {ex.Message}"
                });
                return content;
            }

            if (readResult.TimedOut)
            {
                content.Add(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = $"⚠️ Slide preview unavailable — PowerPoint COM rendering timed out after " +
                               $"{readResult.TimeoutMsUsed / 1000}s. The batch itself was already saved successfully."
                });
                return content;
            }

            if (!readResult.Success || readResult.Slides == null)
            {
                content.Add(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = $"⚠️ Slide preview unavailable: {readResult.ReportText}"
                });
                return content;
            }

            SlideResult? slide = readResult.Slides.FirstOrDefault(s => s.SlideNumber == slideNumber);
            if (slide == null)
            {
                content.Add(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = $"⚠️ Slide preview unavailable — slide {slideNumber} not found among " +
                               $"{readResult.Slides.Count} rendered slide(s)."
                });
                return content;
            }

            content.Add(new JsonObject
            {
                ["type"] = "text",
                ["text"] = $"--- Preview of slide {slideNumber} after batch ---"
            });

            if (slide.PngBytes.Length > 0)
            {
                content.Add(new JsonObject
                {
                    ["type"] = "image",
                    ["source"] = new JsonObject
                    {
                        ["type"] = "base64",
                        ["media_type"] = "image/png",
                        ["data"] = slide.PngBase64
                    }
                });
            }
            else
            {
                content.Add(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = $"(PNG rendering unavailable for slide {slideNumber})"
                });
            }

            return content;
        }

        private static string StripHeader(string reportText)
        {
            int nl = reportText.IndexOf('\n');
            if (nl < 0) return reportText.Trim();
            string firstLine = reportText.Substring(0, nl).TrimStart();
            return firstLine.StartsWith("---", StringComparison.Ordinal)
                ? reportText.Substring(nl + 1).Trim()
                : reportText.Trim();
        }

        private static string AsJsonText(JsonNode? node)
        {
            if (node is null) return "null";
            if (node is JsonValue val && val.TryGetValue(out string? s) && s != null) return s;
            return node.ToJsonString();
        }

        private static PptxOperationResult Fail(string opName, string message)
            => new PptxOperationResult { Success = false, ModifiedBytes = null, ReportText = $"❌ [{opName}] {message}" };

        private static PptxOperationResult Dispatch(string opName, byte[] bytes, int slideNumber, JsonObject op)
        {
            return opName.Trim().ToLowerInvariant() switch
            {
                "text_box" => ApplyTextBox(bytes, slideNumber, op),
                "bullet_list" => ApplyBulletList(bytes, slideNumber, op),
                "table" => ApplyTable(bytes, slideNumber, op),
                "image" => ApplyImage(bytes, slideNumber, op),
                "chart" => ApplyChart(bytes, slideNumber, op),
                "rectangle" => ApplyRectangle(bytes, slideNumber, op),
                "rounded_rectangle" => ApplyRoundedRectangle(bytes, slideNumber, op),
                "diamond" => ApplyDiamond(bytes, slideNumber, op),
                "arrow" => ApplyArrow(bytes, slideNumber, op),
                "connector" => ApplyConnector(bytes, slideNumber, op),
                "elbow_connector" => ApplyElbowConnector(bytes, slideNumber, op),
                "line" => ApplyLine(bytes, slideNumber, op),
                "line_arrow" => ApplyLineArrow(bytes, slideNumber, op),
                _ => Fail(opName, $"unknown 'op' \"{opName}\" — expected one of: text_box, bullet_list, table, image, chart, rectangle, rounded_rectangle, diamond, arrow, connector, elbow_connector, line, line_arrow.")
            };
        }

        private static PptxOperationResult ApplyTextBox(byte[] bytes, int slideNumber, JsonObject op)
        {
            string? shapeName = op["shape_name"]?.GetValue<string>();
            double? top = ToolsIOHelpers.GetOptionalDouble(op["top"]);
            double? left = ToolsIOHelpers.GetOptionalDouble(op["left"]);
            double? bottom = ToolsIOHelpers.GetOptionalDouble(op["bottom"]);
            double? right = ToolsIOHelpers.GetOptionalDouble(op["right"]);
            string? text = op["text"]?.GetValue<string>();

            if (string.IsNullOrWhiteSpace(shapeName)) return Fail("text_box", "'shape_name' is missing or empty.");
            if (top == null || left == null || bottom == null || right == null)
                return Fail("text_box", "'top'/'left'/'bottom'/'right' are required.");
            if (text == null) return Fail("text_box", "'text' is missing.");

            double fontSizePt = ToolsIOHelpers.GetOptionalDouble(op["font_size_pt"]) ?? 18;
            bool bold = ToolsIOHelpers.GetOptionalBool(op, "bold", false);
            bool italic = ToolsIOHelpers.GetOptionalBool(op, "italic", false);
            string? fontColorHex = op["font_color_hex"]?.GetValue<string>();
            string alignment = op["alignment"]?.GetValue<string>() ?? "left";

            return PptxTextBoxTool.SetTextBox(
                bytes, slideNumber, shapeName, top.Value, left.Value, bottom.Value, right.Value,
                text, fontSizePt, bold, italic, fontColorHex, alignment);
        }

        private static PptxOperationResult ApplyBulletList(byte[] bytes, int slideNumber, JsonObject op)
        {
            string? shapeName = op["shape_name"]?.GetValue<string>();
            double? top = ToolsIOHelpers.GetOptionalDouble(op["top"]);
            double? left = ToolsIOHelpers.GetOptionalDouble(op["left"]);
            double? bottom = ToolsIOHelpers.GetOptionalDouble(op["bottom"]);
            double? right = ToolsIOHelpers.GetOptionalDouble(op["right"]);
            JsonNode? itemsNode = op["items"];

            if (string.IsNullOrWhiteSpace(shapeName)) return Fail("bullet_list", "'shape_name' is missing or empty.");
            if (top == null || left == null || bottom == null || right == null)
                return Fail("bullet_list", "'top'/'left'/'bottom'/'right' are required.");
            if (itemsNode == null) return Fail("bullet_list", "'items' is missing.");

            List<(string Text, int IndentLevel)> items;
            try
            {
                items = ToolsPptxHelpers.ParseBulletItems(AsJsonText(itemsNode));
            }
            catch (Exception ex)
            {
                return Fail("bullet_list", $"invalid 'items': {ex.Message}");
            }
            if (items.Count == 0) return Fail("bullet_list", "'items' parsed to zero entries.");

            bool numbered = ToolsIOHelpers.GetOptionalBool(op, "numbered", false);

            return PptxBulletListTool.SetBulletList(
                bytes, slideNumber, shapeName, top.Value, left.Value, bottom.Value, right.Value,
                items, numbered);
        }

        private static PptxOperationResult ApplyTable(byte[] bytes, int slideNumber, JsonObject op)
        {
            string? shapeName = op["shape_name"]?.GetValue<string>();
            double? top = ToolsIOHelpers.GetOptionalDouble(op["top"]);
            double? left = ToolsIOHelpers.GetOptionalDouble(op["left"]);
            double? bottom = ToolsIOHelpers.GetOptionalDouble(op["bottom"]);
            double? right = ToolsIOHelpers.GetOptionalDouble(op["right"]);
            JsonNode? rowsNode = op["rows"];

            if (string.IsNullOrWhiteSpace(shapeName)) return Fail("table", "'shape_name' is missing or empty.");
            if (top == null || left == null || bottom == null || right == null)
                return Fail("table", "'top'/'left'/'bottom'/'right' are required.");
            if (rowsNode == null) return Fail("table", "'rows' is missing.");

            string[][] rows;
            try
            {
                rows = ToolsPptxHelpers.ParseTableRows(AsJsonText(rowsNode));
            }
            catch (Exception ex)
            {
                return Fail("table", $"invalid 'rows': {ex.Message}");
            }

            bool hasHeaderRow = ToolsIOHelpers.GetOptionalBool(op, "has_header_row", true);

            double[]? columnWidths = null;
            JsonNode? columnWidthsNode = op["column_widths"];
            if (columnWidthsNode != null)
            {
                try
                {
                    columnWidths = ToolsPptxHelpers.ParseColumnWidths(AsJsonText(columnWidthsNode));
                }
                catch (Exception ex)
                {
                    return Fail("table", $"invalid 'column_widths': {ex.Message}");
                }
            }

            return PptxTableTool.SetTable(
                bytes, slideNumber, shapeName, top.Value, left.Value, bottom.Value, right.Value,
                rows, hasHeaderRow, columnWidths);
        }

        private static PptxOperationResult ApplyImage(byte[] bytes, int slideNumber, JsonObject op)
        {
            string? shapeName = op["shape_name"]?.GetValue<string>();
            string? imagePathRaw = op["image_path"]?.GetValue<string>();
            double? top = ToolsIOHelpers.GetOptionalDouble(op["top"]);
            double? left = ToolsIOHelpers.GetOptionalDouble(op["left"]);
            double? bottom = ToolsIOHelpers.GetOptionalDouble(op["bottom"]);
            double? right = ToolsIOHelpers.GetOptionalDouble(op["right"]);

            if (string.IsNullOrWhiteSpace(shapeName)) return Fail("image", "'shape_name' is missing or empty.");
            if (string.IsNullOrWhiteSpace(imagePathRaw)) return Fail("image", "'image_path' is missing or empty.");
            if (top == null || left == null || bottom == null || right == null)
                return Fail("image", "'top'/'left'/'bottom'/'right' are required.");

            string imagePath = ToolsIOHelpers.CleanPath(imagePathRaw);
            if (Directory.Exists(imagePath)) return Fail("image", $"'image_path' \"{imagePath}\" is a directory, not a file.");
            if (!File.Exists(imagePath)) return Fail("image", $"'image_path' \"{imagePath}\" was not found.");

            byte[] imageBytes;
            try
            {
                imageBytes = File.ReadAllBytes(imagePath);
            }
            catch (Exception ex)
            {
                return Fail("image", $"error reading 'image_path': {ex.Message}");
            }

            string? imageFormat = op["image_format"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(imageFormat))
            {
                string ext = Path.GetExtension(imagePath).TrimStart('.').ToLowerInvariant();
                imageFormat = ext switch { "png" => "png", "jpg" or "jpeg" => "jpeg", _ => ext };
            }

            return PptxImageTool.SetImage(
                bytes, slideNumber, shapeName, imageBytes, imageFormat, top.Value, left.Value,
                bottom.Value, right.Value);
        }

        private static PptxOperationResult ApplyChart(byte[] bytes, int slideNumber, JsonObject op)
        {
            string? shapeName = op["shape_name"]?.GetValue<string>();
            double? top = ToolsIOHelpers.GetOptionalDouble(op["top"]);
            double? left = ToolsIOHelpers.GetOptionalDouble(op["left"]);
            double? bottom = ToolsIOHelpers.GetOptionalDouble(op["bottom"]);
            double? right = ToolsIOHelpers.GetOptionalDouble(op["right"]);
            string? chartType = op["chart_type"]?.GetValue<string>();
            JsonNode? categoriesNode = op["categories"];
            JsonNode? seriesNode = op["series"];

            if (string.IsNullOrWhiteSpace(shapeName)) return Fail("chart", "'shape_name' is missing or empty.");
            if (top == null || left == null || bottom == null || right == null)
                return Fail("chart", "'top'/'left'/'bottom'/'right' are required.");
            if (string.IsNullOrWhiteSpace(chartType)) return Fail("chart", "'chart_type' is missing or empty.");
            if (categoriesNode == null) return Fail("chart", "'categories' is missing.");
            if (seriesNode == null) return Fail("chart", "'series' is missing.");

            string[] categories;
            (string Name, double[] Values)[] series;
            try
            {
                categories = ToolsPptxHelpers.ParseChartCategories(AsJsonText(categoriesNode));
            }
            catch (Exception ex)
            {
                return Fail("chart", $"invalid 'categories': {ex.Message}");
            }
            try
            {
                series = ToolsPptxHelpers.ParseChartSeries(AsJsonText(seriesNode));
            }
            catch (Exception ex)
            {
                return Fail("chart", $"invalid 'series': {ex.Message}");
            }

            string? title = op["title"]?.GetValue<string>();
            bool showLegend = ToolsIOHelpers.GetOptionalBool(op, "show_legend", true);

            return PptxChartTool.SetChart(
                bytes, slideNumber, shapeName, top.Value, left.Value, bottom.Value, right.Value,
                chartType!, categories, series, title, showLegend);
        }

        private static PptxOperationResult ApplyRectangle(byte[] bytes, int slideNumber, JsonObject op)
        {
            string? shapeName = op["shape_name"]?.GetValue<string>();
            double? top = ToolsIOHelpers.GetOptionalDouble(op["top"]);
            double? left = ToolsIOHelpers.GetOptionalDouble(op["left"]);
            double? bottom = ToolsIOHelpers.GetOptionalDouble(op["bottom"]);
            double? right = ToolsIOHelpers.GetOptionalDouble(op["right"]);

            if (string.IsNullOrWhiteSpace(shapeName)) return Fail("rectangle", "'shape_name' is missing or empty.");
            if (top == null || left == null || bottom == null || right == null)
                return Fail("rectangle", "'top'/'left'/'bottom'/'right' are required.");

            string? fillColorHex = op["fill_color_hex"]?.GetValue<string>();
            string? text = op["text"]?.GetValue<string>();
            string? fontColorHex = op["font_color_hex"]?.GetValue<string>();

            return PptxShapeRectangleTool.SetRectangle(
                bytes, slideNumber, shapeName, top.Value, left.Value, bottom.Value, right.Value,
                fillColorHex, text, fontColorHex: fontColorHex);
        }

        private static PptxOperationResult ApplyRoundedRectangle(byte[] bytes, int slideNumber, JsonObject op)
        {
            string? shapeName = op["shape_name"]?.GetValue<string>();
            double? top = ToolsIOHelpers.GetOptionalDouble(op["top"]);
            double? left = ToolsIOHelpers.GetOptionalDouble(op["left"]);
            double? bottom = ToolsIOHelpers.GetOptionalDouble(op["bottom"]);
            double? right = ToolsIOHelpers.GetOptionalDouble(op["right"]);

            if (string.IsNullOrWhiteSpace(shapeName)) return Fail("rounded_rectangle", "'shape_name' is missing or empty.");
            if (top == null || left == null || bottom == null || right == null)
                return Fail("rounded_rectangle", "'top'/'left'/'bottom'/'right' are required.");

            string? fillColorHex = op["fill_color_hex"]?.GetValue<string>();
            string? text = op["text"]?.GetValue<string>();
            double? cornerRoundness = ToolsIOHelpers.GetOptionalDouble(op["corner_roundness"]);
            string? fontColorHex = op["font_color_hex"]?.GetValue<string>();

            return PptxShapeRoundedRectangleTool.SetShapeRoundedRectangle(
                bytes, slideNumber, shapeName, top.Value, left.Value, bottom.Value, right.Value,
                fillColorHex, text, cornerRoundness, fontColorHex: fontColorHex);
        }

        private static PptxOperationResult ApplyDiamond(byte[] bytes, int slideNumber, JsonObject op)
        {
            string? shapeName = op["shape_name"]?.GetValue<string>();
            double? top = ToolsIOHelpers.GetOptionalDouble(op["top"]);
            double? left = ToolsIOHelpers.GetOptionalDouble(op["left"]);
            double? bottom = ToolsIOHelpers.GetOptionalDouble(op["bottom"]);
            double? right = ToolsIOHelpers.GetOptionalDouble(op["right"]);

            if (string.IsNullOrWhiteSpace(shapeName)) return Fail("diamond", "'shape_name' is missing or empty.");
            if (top == null || left == null || bottom == null || right == null)
                return Fail("diamond", "'top'/'left'/'bottom'/'right' are required.");

            string? fillColorHex = op["fill_color_hex"]?.GetValue<string>();
            string? text = op["text"]?.GetValue<string>();
            string? fontColorHex = op["font_color_hex"]?.GetValue<string>();

            return PptxShapeDiamondTool.SetShapeDiamond(
                bytes, slideNumber, shapeName, top.Value, left.Value, bottom.Value, right.Value,
                fillColorHex, text, fontColorHex: fontColorHex);
        }

        private static PptxOperationResult ApplyArrow(byte[] bytes, int slideNumber, JsonObject op)
        {
            string? shapeName = op["shape_name"]?.GetValue<string>();
            double? top = ToolsIOHelpers.GetOptionalDouble(op["top"]);
            double? left = ToolsIOHelpers.GetOptionalDouble(op["left"]);
            double? bottom = ToolsIOHelpers.GetOptionalDouble(op["bottom"]);
            double? right = ToolsIOHelpers.GetOptionalDouble(op["right"]);

            if (string.IsNullOrWhiteSpace(shapeName)) return Fail("arrow", "'shape_name' is missing or empty.");
            if (top == null || left == null || bottom == null || right == null)
                return Fail("arrow", "'top'/'left'/'bottom'/'right' are required.");

            double rotationDegrees = ToolsIOHelpers.GetOptionalDouble(op["rotation_degrees"]) ?? 0;
            string? fillColorHex = op["fill_color_hex"]?.GetValue<string>();
            string? text = op["text"]?.GetValue<string>();

            return PptxShapeArrowTool.SetShapeArrow(
                bytes, slideNumber, shapeName, top.Value, left.Value, bottom.Value, right.Value,
                rotationDegrees, fillColorHex, text);
        }

        private static PptxOperationResult ApplyConnector(byte[] bytes, int slideNumber, JsonObject op)
        {
            string? shapeName = op["shape_name"]?.GetValue<string>();
            string? fromShapeName = op["from_shape_name"]?.GetValue<string>();
            int? fromSiteIndex = ToolsIOHelpers.GetOptionalInt(op["from_connection_site_index"]);
            string? toShapeName = op["to_shape_name"]?.GetValue<string>();
            int? toSiteIndex = ToolsIOHelpers.GetOptionalInt(op["to_connection_site_index"]);

            if (string.IsNullOrWhiteSpace(shapeName)) return Fail("connector", "'shape_name' is missing or empty.");
            if (string.IsNullOrWhiteSpace(fromShapeName)) return Fail("connector", "'from_shape_name' is missing or empty.");
            if (fromSiteIndex == null) return Fail("connector", "'from_connection_site_index' is missing.");
            if (string.IsNullOrWhiteSpace(toShapeName)) return Fail("connector", "'to_shape_name' is missing or empty.");
            if (toSiteIndex == null) return Fail("connector", "'to_connection_site_index' is missing.");

            bool hasArrow = ToolsIOHelpers.GetOptionalBool(op, "has_arrow", true);
            double lineWidthPt = ToolsIOHelpers.GetOptionalDouble(op["line_width_pt"]) ?? 1.5;
            string? lineColorHex = op["line_color_hex"]?.GetValue<string>();
            string arrowStyle = op["arrow_style"]?.GetValue<string>() ?? "triangle";
            string lineDashStyle = op["line_dash_style"]?.GetValue<string>() ?? "solid";

            return PptxShapeConnectorTool.SetShapeConnector(
                bytes, slideNumber, shapeName, fromShapeName, fromSiteIndex.Value,
                toShapeName, toSiteIndex.Value, hasArrow, lineWidthPt, lineColorHex, arrowStyle, lineDashStyle);
        }

        private static PptxOperationResult ApplyElbowConnector(byte[] bytes, int slideNumber, JsonObject op)
        {
            string? shapeName = op["shape_name"]?.GetValue<string>();
            double? startX = ToolsIOHelpers.GetOptionalDouble(op["start_x"]);
            double? startY = ToolsIOHelpers.GetOptionalDouble(op["start_y"]);
            double? endX = ToolsIOHelpers.GetOptionalDouble(op["end_x"]);
            double? endY = ToolsIOHelpers.GetOptionalDouble(op["end_y"]);

            if (string.IsNullOrWhiteSpace(shapeName)) return Fail("elbow_connector", "'shape_name' is missing or empty.");
            if (startX == null || startY == null || endX == null || endY == null)
                return Fail("elbow_connector", "'start_x'/'start_y'/'end_x'/'end_y' are required.");

            bool hasArrow = ToolsIOHelpers.GetOptionalBool(op, "has_arrow", true);
            double lineWidthPt = ToolsIOHelpers.GetOptionalDouble(op["line_width_pt"]) ?? 1.5;
            string? lineColorHex = op["line_color_hex"]?.GetValue<string>();
            string arrowStyle = op["arrow_style"]?.GetValue<string>() ?? "triangle";
            string lineDashStyle = op["line_dash_style"]?.GetValue<string>() ?? "solid";
            double? loopClearance = ToolsIOHelpers.GetOptionalDouble(op["loop_clearance"]);

            (double X, double Y)[]? checkpoints = null;
            JsonNode? checkpointsNode = op["checkpoints"];
            if (checkpointsNode != null)
            {
                try
                {
                    checkpoints = ToolsPptxHelpers.ParseCheckpoints(AsJsonText(checkpointsNode));
                }
                catch (Exception ex)
                {
                    return Fail("elbow_connector", $"invalid 'checkpoints': {ex.Message}");
                }
            }

            return PptxShapeElbowConnectorTool.SetShapeElbowConnector(
                bytes, slideNumber, shapeName, startX.Value, startY.Value, endX.Value, endY.Value,
                hasArrow, lineWidthPt, lineColorHex, arrowStyle, lineDashStyle, loopClearance, checkpoints);
        }

        private static PptxOperationResult ApplyLine(byte[] bytes, int slideNumber, JsonObject op)
        {
            string? shapeName = op["shape_name"]?.GetValue<string>();
            double? startX = ToolsIOHelpers.GetOptionalDouble(op["start_x"]);
            double? startY = ToolsIOHelpers.GetOptionalDouble(op["start_y"]);
            double? endX = ToolsIOHelpers.GetOptionalDouble(op["end_x"]);
            double? endY = ToolsIOHelpers.GetOptionalDouble(op["end_y"]);

            if (string.IsNullOrWhiteSpace(shapeName)) return Fail("line", "'shape_name' is missing or empty.");
            if (startX == null || startY == null || endX == null || endY == null)
                return Fail("line", "'start_x'/'start_y'/'end_x'/'end_y' are required.");

            double lineWidthPt = ToolsIOHelpers.GetOptionalDouble(op["line_width_pt"]) ?? 1.5;
            string? lineColorHex = op["line_color_hex"]?.GetValue<string>();
            string lineDashStyle = op["line_dash_style"]?.GetValue<string>() ?? "solid";

            return PptxShapeLineTool.SetShapeLine(
                bytes, slideNumber, shapeName, startX.Value, startY.Value, endX.Value, endY.Value,
                lineWidthPt, lineColorHex, lineDashStyle);
        }

        private static PptxOperationResult ApplyLineArrow(byte[] bytes, int slideNumber, JsonObject op)
        {
            string? shapeName = op["shape_name"]?.GetValue<string>();
            double? startX = ToolsIOHelpers.GetOptionalDouble(op["start_x"]);
            double? startY = ToolsIOHelpers.GetOptionalDouble(op["start_y"]);
            double? endX = ToolsIOHelpers.GetOptionalDouble(op["end_x"]);
            double? endY = ToolsIOHelpers.GetOptionalDouble(op["end_y"]);

            if (string.IsNullOrWhiteSpace(shapeName)) return Fail("line_arrow", "'shape_name' is missing or empty.");
            if (startX == null || startY == null || endX == null || endY == null)
                return Fail("line_arrow", "'start_x'/'start_y'/'end_x'/'end_y' are required.");

            double lineWidthPt = ToolsIOHelpers.GetOptionalDouble(op["line_width_pt"]) ?? 1.5;
            string? lineColorHex = op["line_color_hex"]?.GetValue<string>();
            string arrowStyle = op["arrow_style"]?.GetValue<string>() ?? "triangle";
            string lineDashStyle = op["line_dash_style"]?.GetValue<string>() ?? "solid";

            return PptxShapeLineArrowTool.SetShapeLineArrow(
                bytes, slideNumber, shapeName, startX.Value, startY.Value, endX.Value, endY.Value,
                lineWidthPt, lineColorHex, arrowStyle, lineDashStyle);
        }
    }
}
