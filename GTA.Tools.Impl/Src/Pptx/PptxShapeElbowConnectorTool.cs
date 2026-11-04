using System;
using System.Collections.Generic;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using P = DocumentFormat.OpenXml.Presentation;
using A = DocumentFormat.OpenXml.Drawing;

namespace PptxModifierLib
{
    public static class PptxShapeElbowConnectorTool
    {
        public static PptxOperationResult SetShapeElbowConnector(
            byte[] pptxBytes,
            int slideNumber,
            string shapeName,
            double startX,
            double startY,
            double endX,
            double endY,
            bool hasArrow = true,
            double lineWidthPt = 1.5,
            string? lineColorHex = null,
            string arrowStyle = "triangle",
            string lineDashStyle = "solid",
            double? loopClearance = null,
            IReadOnlyList<(double X, double Y)>? checkpoints = null)
        {
            var report = new StringBuilder();
            report.AppendLine("--- pptx_shape_elbow_connector ---");

            if (pptxBytes == null || pptxBytes.Length == 0)
            {
                report.AppendLine("❌ No presentation bytes supplied.");
                return Fail(report);
            }
            if (string.IsNullOrWhiteSpace(shapeName))
            {
                report.AppendLine("❌ shapeName must not be empty — it is the stable key used to find/update this connector on later calls.");
                return Fail(report);
            }
            if (lineWidthPt <= 0)
            {
                report.AppendLine("❌ lineWidthPt must be positive.");
                return Fail(report);
            }

            bool useLiteralCheckpoints = checkpoints != null && checkpoints.Count > 0;

            double normalizedClearance = loopClearance ?? 0.0667;
            if (!useLiteralCheckpoints && normalizedClearance <= 0)
            {
                report.AppendLine("❌ loopClearance must be positive (normalized fraction of the slide's height).");
                return Fail(report);
            }

            string? normalizedColor = null;
            if (lineColorHex != null)
            {
                normalizedColor = PptxCommon.NormalizeHexColor(lineColorHex);
                if (normalizedColor == null)
                {
                    report.AppendLine($"❌ \"{lineColorHex}\" is not a valid hex color (expected e.g. \"FF0000\" or \"#FF0000\").");
                    return Fail(report);
                }
            }

            A.LineEndValues? arrowValue = null;
            if (hasArrow)
            {
                arrowValue = PptxShapeCommon.NormalizeArrowStyle(arrowStyle, out string? arrowError);
                if (arrowError != null)
                {
                    report.AppendLine($"❌ {arrowError}");
                    return Fail(report);
                }
            }

            A.PresetLineDashValues? dashValue = PptxShapeCommon.NormalizeDashStyle(lineDashStyle, out string? dashError);
            if (dashError != null)
            {
                report.AppendLine($"❌ {dashError}");
                return Fail(report);
            }

            try
            {
                bool created = false;
                long startXEmu = 0, startYEmu = 0, endXEmu = 0, endYEmu = 0, clearanceEmu = 0;

                byte[] result = PptxCommon.EditInMemory(pptxBytes, doc =>
                {
                    PresentationPart presentationPart = doc.PresentationPart
                        ?? throw new InvalidOperationException("this file has no presentation part — is it a valid .pptx?");

                    var slides = PptxCommon.GetOrderedSlideParts(presentationPart);
                    if (slideNumber < 1 || slideNumber > slides.Count)
                        throw new InvalidOperationException(
                            $"slideNumber {slideNumber} is out of range — this presentation has {slides.Count} slide(s).");

                    SlidePart slidePart = slides[slideNumber - 1];
                    P.ShapeTree shapeTree = slidePart.Slide.CommonSlideData?.ShapeTree
                        ?? throw new InvalidOperationException("this slide has no shape tree (malformed slide XML).");

                    var (slideWidthEmu, slideHeightEmu) = PptxNormalizedCoordinates.GetSlideSizeEmu(presentationPart);
                    (startXEmu, startYEmu) = PptxNormalizedCoordinates.PointToEmu(startX, startY, slideWidthEmu, slideHeightEmu);
                    (endXEmu, endYEmu) = PptxNormalizedCoordinates.PointToEmu(endX, endY, slideWidthEmu, slideHeightEmu);

                    var points = new List<(long X, long Y)>();

                    if (useLiteralCheckpoints)
                    {
                        // MODE 1 — literal start -> checkpoints... -> end, straight
                        // segments through each waypoint, exactly as supplied.
                        points.Add((startXEmu, startYEmu));
                        foreach (var cp in checkpoints!)
                        {
                            var (cpXEmu, cpYEmu) = PptxNormalizedCoordinates.PointToEmu(cp.X, cp.Y, slideWidthEmu, slideHeightEmu);
                            points.Add((cpXEmu, cpYEmu));
                        }
                        points.Add((endXEmu, endYEmu));
                    }
                    else
                    {
                        // MODE 2 — original auto-computed "circling to the bottom"
                        // loop-back route, unchanged.
                        clearanceEmu = PptxNormalizedCoordinates.LengthToEmuByHeight(normalizedClearance, slideHeightEmu);

                        long bottomY = Math.Max(startYEmu, endYEmu) + clearanceEmu;
                        long exitX = startXEmu <= endXEmu ? startXEmu - clearanceEmu : startXEmu + clearanceEmu;
                        long approachX = startXEmu <= endXEmu ? endXEmu + clearanceEmu : endXEmu - clearanceEmu;

                        points.Add((startXEmu, startYEmu));
                        points.Add((exitX, startYEmu));
                        points.Add((exitX, bottomY));
                        points.Add((approachX, bottomY));
                        points.Add((approachX, endYEmu));
                        points.Add((endXEmu, endYEmu));
                    }

                    var (geometry, bounds) = PptxShapeCommon.BuildMultiPointPathGeometry(points);
                    A.Outline outline = PptxShapeCommon.BuildLineOutline(
                        lineWidthPt, normalizedColor, dashValue, headArrowType: null, tailArrowType: arrowValue);

                    created = PptxShapeCommon.UpsertCustomPathShape(shapeTree, shapeName, bounds, geometry, outline);

                    slidePart.Slide.Save();
                });

                string colorNote = normalizedColor != null ? $", color #{normalizedColor}" : "";
                string dashNote = lineDashStyle != "solid" ? $", dash \"{lineDashStyle}\"" : "";
                string arrowNote = hasArrow ? $", arrow \"{arrowStyle}\"" : ", no arrowhead";
                string routeNote = useLiteralCheckpoints
                    ? $"literal path through {checkpoints!.Count} checkpoint(s)"
                    : $"circling below with clearance {normalizedClearance:0.####} -> {clearanceEmu} EMU";
                report.AppendLine(created
                    ? $"✅ Created elbow connector \"{shapeName}\" on slide {slideNumber} — normalized ({startX:0.####},{startY:0.####}) to ({endX:0.####},{endY:0.####}) -> EMU ({startXEmu},{startYEmu}) to ({endXEmu},{endYEmu}), {routeNote}, width {lineWidthPt}pt{arrowNote}{colorNote}{dashNote}."
                    : $"✅ Updated elbow connector \"{shapeName}\" on slide {slideNumber} — normalized ({startX:0.####},{startY:0.####}) to ({endX:0.####},{endY:0.####}) -> EMU ({startXEmu},{startYEmu}) to ({endXEmu},{endYEmu}), {routeNote}, width {lineWidthPt}pt{arrowNote}{colorNote}{dashNote}.");

                return new PptxOperationResult { Success = true, ModifiedBytes = result, ReportText = report.ToString().TrimEnd() };
            }
            catch (Exception ex)
            {
                report.AppendLine($"❌ Fatal error: {ex.Message}");
                return Fail(report);
            }
        }

        private static PptxOperationResult Fail(StringBuilder report)
            => new PptxOperationResult { Success = false, ModifiedBytes = null, ReportText = report.ToString().TrimEnd() };

        /// <summary>Base64 convenience wrapper — mirrors XlsxModifierTool's *FromBase64 pattern.</summary>
        public static PptxOperationResult SetShapeElbowConnectorFromBase64(
            string base64,
            int slideNumber,
            string shapeName,
            double startX,
            double startY,
            double endX,
            double endY,
            bool hasArrow = true,
            double lineWidthPt = 1.5,
            string? lineColorHex = null,
            string arrowStyle = "triangle",
            string lineDashStyle = "solid",
            double? loopClearance = null,
            IReadOnlyList<(double X, double Y)>? checkpoints = null)
        {
            if (string.IsNullOrWhiteSpace(base64))
                throw new ArgumentException("base64 must not be null or empty.", nameof(base64));
            return SetShapeElbowConnector(Convert.FromBase64String(base64), slideNumber, shapeName,
                startX, startY, endX, endY, hasArrow, lineWidthPt, lineColorHex, arrowStyle, lineDashStyle,
                loopClearance, checkpoints);
        }
    }
}
