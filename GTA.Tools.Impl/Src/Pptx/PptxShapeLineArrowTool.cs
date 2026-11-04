using System;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using P = DocumentFormat.OpenXml.Presentation;
using A = DocumentFormat.OpenXml.Drawing;

namespace PptxModifierLib
{
    public static class PptxShapeLineArrowTool
    {
        public static PptxOperationResult SetShapeLineArrow(
            byte[] pptxBytes,
            int slideNumber,
            string shapeName,
            double startX,
            double startY,
            double endX,
            double endY,
            double lineWidthPt = 1.5,
            string? lineColorHex = null,
            string arrowStyle = "triangle",
            string lineDashStyle = "solid")
        {
            var report = new StringBuilder();
            report.AppendLine("--- pptx_shape_line_arrow ---");

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

            A.LineEndValues? arrowValue = PptxShapeCommon.NormalizeArrowStyle(arrowStyle, out string? arrowError);
            if (arrowError != null)
            {
                report.AppendLine($"❌ {arrowError}");
                return Fail(report);
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
                long startXEmu = 0, startYEmu = 0, endXEmu = 0, endYEmu = 0;

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

                    var bounds = PptxShapeCommon.ComputeConnectorBoundsAndFlip(startXEmu, startYEmu, endXEmu, endYEmu);
                    A.Outline outline = PptxShapeCommon.BuildLineOutline(
                        lineWidthPt, normalizedColor, dashValue, headArrowType: null, tailArrowType: arrowValue);

                    var lineGeometry = new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Line };
                    created = PptxShapeCommon.UpsertConnector(
                        shapeTree, shapeName, bounds, lineGeometry, outline);

                    slidePart.Slide.Save();
                });

                string colorNote = normalizedColor != null ? $", color #{normalizedColor}" : "";
                string dashNote = lineDashStyle != "solid" ? $", dash \"{lineDashStyle}\"" : "";
                report.AppendLine(created
                    ? $"✅ Created line arrow \"{shapeName}\" on slide {slideNumber} — normalized ({startX:0.####},{startY:0.####}) to ({endX:0.####},{endY:0.####}) -> EMU ({startXEmu},{startYEmu}) to ({endXEmu},{endYEmu}), width {lineWidthPt}pt, arrow \"{arrowStyle}\"{colorNote}{dashNote}."
                    : $"✅ Updated line arrow \"{shapeName}\" on slide {slideNumber} — normalized ({startX:0.####},{startY:0.####}) to ({endX:0.####},{endY:0.####}) -> EMU ({startXEmu},{startYEmu}) to ({endXEmu},{endYEmu}), width {lineWidthPt}pt, arrow \"{arrowStyle}\"{colorNote}{dashNote}.");

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
        public static PptxOperationResult SetShapeLineArrowFromBase64(
            string base64,
            int slideNumber,
            string shapeName,
            double startX,
            double startY,
            double endX,
            double endY,
            double lineWidthPt = 1.5,
            string? lineColorHex = null,
            string arrowStyle = "triangle",
            string lineDashStyle = "solid")
        {
            if (string.IsNullOrWhiteSpace(base64))
                throw new ArgumentException("base64 must not be null or empty.", nameof(base64));
            return SetShapeLineArrow(Convert.FromBase64String(base64), slideNumber, shapeName,
                startX, startY, endX, endY, lineWidthPt, lineColorHex, arrowStyle, lineDashStyle);
        }
    }
}
