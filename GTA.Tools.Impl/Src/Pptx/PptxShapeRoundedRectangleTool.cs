using System;
using System.Collections.Generic;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using P = DocumentFormat.OpenXml.Presentation;
using A = DocumentFormat.OpenXml.Drawing;

namespace PptxModifierLib
{
    public static class PptxShapeRoundedRectangleTool
    {
        public static PptxOperationResult SetShapeRoundedRectangle(
            byte[] pptxBytes,
            int slideNumber,
            string shapeName,
            double top,
            double left,
            double bottom,
            double right,
            string? fillColorHex = null,
            string? text = null,
            double? cornerRoundness = null,
            double? fontSizePt = null,
            string? horizontalAlign = null,
            string? verticalAlign = null,
            string? fontColorHex = null)
        {
            var report = new StringBuilder();
            report.AppendLine("--- pptx_shape_rounded_rectangle ---");

            if (pptxBytes == null || pptxBytes.Length == 0)
            {
                report.AppendLine("❌ No presentation bytes supplied.");
                return Fail(report);
            }
            if (string.IsNullOrWhiteSpace(shapeName))
            {
                report.AppendLine("❌ shapeName must not be empty — it is the stable key used to find/update this shape on later calls.");
                return Fail(report);
            }
            if (right <= left || bottom <= top)
            {
                report.AppendLine("❌ right must be greater than left, and bottom must be greater than top (normalized fractions of the slide's size).");
                return Fail(report);
            }
            if (cornerRoundness.HasValue && (cornerRoundness.Value < 0.0 || cornerRoundness.Value > 0.5))
            {
                report.AppendLine("❌ cornerRoundness must be between 0.0 and 0.5.");
                return Fail(report);
            }

            string? normalizedColor = null;
            if (fillColorHex != null)
            {
                normalizedColor = PptxCommon.NormalizeHexColor(fillColorHex);
                if (normalizedColor == null)
                {
                    report.AppendLine($"❌ \"{fillColorHex}\" is not a valid hex color (expected e.g. \"FF0000\" or \"#FF0000\").");
                    return Fail(report);
                }
            }

            string? normalizedFontColor = null;
            if (fontColorHex != null)
            {
                normalizedFontColor = PptxCommon.NormalizeHexColor(fontColorHex);
                if (normalizedFontColor == null)
                {
                    report.AppendLine($"❌ \"{fontColorHex}\" is not a valid hex color (expected e.g. \"FF0000\" or \"#FF0000\").");
                    return Fail(report);
                }
            }
            A.TextAlignmentTypeValues? normalizedHorizontalAlign = PptxShapeCommon.NormalizeHorizontalAlign(horizontalAlign, out string? horizontalAlignError);
            if (horizontalAlignError != null)
            {
                report.AppendLine($"❌ {horizontalAlignError}");
                return Fail(report);
            }

            A.TextAnchoringTypeValues? normalizedVerticalAlign = PptxShapeCommon.NormalizeVerticalAlign(verticalAlign, out string? verticalAlignError);
            if (verticalAlignError != null)
            {
                report.AppendLine($"❌ {verticalAlignError}");
                return Fail(report);
            }

            List<A.ShapeGuide>? adjustValues = null;
            if (cornerRoundness.HasValue)
            {
                long raw = PptxShapeCommon.RoundnessToAdjustRawValue(cornerRoundness.Value);
                adjustValues = new List<A.ShapeGuide> { new A.ShapeGuide { Name = "adj", Formula = $"val {raw}" } };
            }

            try
            {
                bool created = false;
                long offsetXEmu = 0, offsetYEmu = 0, widthEmu = 0, heightEmu = 0;

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
                    (offsetXEmu, offsetYEmu, widthEmu, heightEmu) = PptxNormalizedCoordinates.RectFromEdgesToEmu(
                        top, left, bottom, right, slideWidthEmu, slideHeightEmu);

                    created = PptxShapeCommon.UpsertBoundedShape(
                        shapeTree, shapeName,
                        offsetXEmu, offsetYEmu, widthEmu, heightEmu,
                        A.ShapeTypeValues.RoundRectangle,
                        adjustValues,
                        rotationRaw: null,
                        normalizedFillColorHex: normalizedColor,
                        text: text,
                        fontSizePt: fontSizePt,
                        horizontalAlign: normalizedHorizontalAlign,
                        verticalAlign: normalizedVerticalAlign,
                        normalizedFontColorHex: normalizedFontColor);

                    slidePart.Slide.Save();
                });

                string colorNote = normalizedColor != null ? $", fill #{normalizedColor}" : "";
                string roundnessNote = cornerRoundness.HasValue ? $", cornerRoundness {cornerRoundness.Value:0.###}" : "";
                string fontNote = fontSizePt.HasValue ? $", font {fontSizePt.Value:0.#}pt" : "";
                string fontColorNote = normalizedFontColor != null ? $", font color #{normalizedFontColor}" : "";
                string alignNote = (horizontalAlign != null || verticalAlign != null)
                    ? $", align ({horizontalAlign ?? "default"}, {verticalAlign ?? "default"})" : "";
                report.AppendLine(created
                    ? $"✅ Created rounded rectangle \"{shapeName}\" on slide {slideNumber} — normalized edges top={top:0.####},left={left:0.####},bottom={bottom:0.####},right={right:0.####} -> EMU pos ({offsetXEmu},{offsetYEmu}), size ({widthEmu}x{heightEmu}){colorNote}{roundnessNote}{fontNote}{fontColorNote}{alignNote}."
                    : $"✅ Updated rounded rectangle \"{shapeName}\" on slide {slideNumber} — normalized edges top={top:0.####},left={left:0.####},bottom={bottom:0.####},right={right:0.####} -> EMU pos ({offsetXEmu},{offsetYEmu}), size ({widthEmu}x{heightEmu}){colorNote}{roundnessNote}{fontNote}{fontColorNote}{alignNote}.");

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
        public static PptxOperationResult SetShapeRoundedRectangleFromBase64(
            string base64,
            int slideNumber,
            string shapeName,
            double top,
            double left,
            double bottom,
            double right,
            string? fillColorHex = null,
            string? text = null,
            double? cornerRoundness = null,
            double? fontSizePt = null,
            string? horizontalAlign = null,
            string? verticalAlign = null,
            string? fontColorHex = null)
        {
            if (string.IsNullOrWhiteSpace(base64))
                throw new ArgumentException("base64 must not be null or empty.", nameof(base64));
            return SetShapeRoundedRectangle(Convert.FromBase64String(base64), slideNumber, shapeName,
                top, left, bottom, right, fillColorHex, text, cornerRoundness, fontSizePt, horizontalAlign, verticalAlign, fontColorHex);
        }
    }
}
