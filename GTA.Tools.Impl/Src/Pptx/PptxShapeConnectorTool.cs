using System;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using P = DocumentFormat.OpenXml.Presentation;
using A = DocumentFormat.OpenXml.Drawing;

namespace PptxModifierLib
{
    public static class PptxShapeConnectorTool
    {
        public static PptxOperationResult SetShapeConnector(
            byte[] pptxBytes,
            int slideNumber,
            string shapeName,
            string fromShapeName,
            int fromConnectionSiteIndex,
            string toShapeName,
            int toConnectionSiteIndex,
            bool hasArrow = true,
            double lineWidthPt = 1.5,
            string? lineColorHex = null,
            string arrowStyle = "triangle",
            string lineDashStyle = "solid")
        {
            var report = new StringBuilder();
            report.AppendLine("--- pptx_shape_connector ---");

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
            if (string.IsNullOrWhiteSpace(fromShapeName) || string.IsNullOrWhiteSpace(toShapeName))
            {
                report.AppendLine("❌ fromShapeName/toShapeName must not be empty.");
                return Fail(report);
            }
            if (fromConnectionSiteIndex < 0 || fromConnectionSiteIndex > 3)
            {
                report.AppendLine("❌ fromConnectionSiteIndex must be 0 (top), 1 (right), 2 (bottom), or 3 (left).");
                return Fail(report);
            }
            if (toConnectionSiteIndex < 0 || toConnectionSiteIndex > 3)
            {
                report.AppendLine("❌ toConnectionSiteIndex must be 0 (top), 1 (right), 2 (bottom), or 3 (left).");
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

                    P.Shape fromShape = PptxCommon.FindShapeByName(shapeTree, fromShapeName)
                        ?? throw new InvalidOperationException($"fromShapeName \"{fromShapeName}\" was not found on slide {slideNumber}.");
                    P.Shape toShape = PptxCommon.FindShapeByName(shapeTree, toShapeName)
                        ?? throw new InvalidOperationException($"toShapeName \"{toShapeName}\" was not found on slide {slideNumber}.");

                    uint fromShapeId = fromShape.NonVisualShapeProperties?.NonVisualDrawingProperties?.Id?.Value
                        ?? throw new InvalidOperationException($"fromShapeName \"{fromShapeName}\" has no shape id (malformed slide XML).");
                    uint toShapeId = toShape.NonVisualShapeProperties?.NonVisualDrawingProperties?.Id?.Value
                        ?? throw new InvalidOperationException($"toShapeName \"{toShapeName}\" has no shape id (malformed slide XML).");

                    (long fromX, long fromY) = PptxShapeCommon.ResolveConnectionSitePoint(fromShape, fromConnectionSiteIndex);
                    (long toX, long toY) = PptxShapeCommon.ResolveConnectionSitePoint(toShape, toConnectionSiteIndex);

                    var bounds = PptxShapeCommon.ComputeConnectorBoundsAndFlip(fromX, fromY, toX, toY);
                    A.Outline outline = PptxShapeCommon.BuildLineOutline(
                        lineWidthPt, normalizedColor, dashValue, headArrowType: null, tailArrowType: arrowValue);
                    var geometry = new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Line };

                    created = PptxShapeCommon.UpsertConnector(shapeTree, shapeName, bounds, geometry, outline);

                    P.ConnectionShape connector = PptxShapeCommon.FindConnectorByName(shapeTree, shapeName)
                        ?? throw new InvalidOperationException("internal error: connector was just upserted but could not be found by name immediately afterward.");
                    P.NonVisualConnectorShapeDrawingProperties cNvCxnSpPr =
                        connector.NonVisualConnectionShapeProperties!.NonVisualConnectorShapeDrawingProperties!;
                    cNvCxnSpPr.StartConnection = new A.StartConnection { Id = fromShapeId, Index = (uint)fromConnectionSiteIndex };
                    cNvCxnSpPr.EndConnection = new A.EndConnection { Id = toShapeId, Index = (uint)toConnectionSiteIndex };

                    slidePart.Slide.Save();
                });

                string colorNote = normalizedColor != null ? $", color #{normalizedColor}" : "";
                string dashNote = lineDashStyle != "solid" ? $", dash \"{lineDashStyle}\"" : "";
                string arrowNote = hasArrow ? $", arrow \"{arrowStyle}\"" : ", no arrowhead";
                string glueNote = $"glued {fromShapeName}[site {fromConnectionSiteIndex}] -> {toShapeName}[site {toConnectionSiteIndex}]";
                report.AppendLine(created
                    ? $"✅ Created connector \"{shapeName}\" on slide {slideNumber} — {glueNote}, width {lineWidthPt}pt{arrowNote}{colorNote}{dashNote}."
                    : $"✅ Updated connector \"{shapeName}\" on slide {slideNumber} — {glueNote}, width {lineWidthPt}pt{arrowNote}{colorNote}{dashNote}.");

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
        public static PptxOperationResult SetShapeConnectorFromBase64(
            string base64,
            int slideNumber,
            string shapeName,
            string fromShapeName,
            int fromConnectionSiteIndex,
            string toShapeName,
            int toConnectionSiteIndex,
            bool hasArrow = true,
            double lineWidthPt = 1.5,
            string? lineColorHex = null,
            string arrowStyle = "triangle",
            string lineDashStyle = "solid")
        {
            if (string.IsNullOrWhiteSpace(base64))
                throw new ArgumentException("base64 must not be null or empty.", nameof(base64));
            return SetShapeConnector(Convert.FromBase64String(base64), slideNumber, shapeName,
                fromShapeName, fromConnectionSiteIndex, toShapeName, toConnectionSiteIndex,
                hasArrow, lineWidthPt, lineColorHex, arrowStyle, lineDashStyle);
        }
    }
}
