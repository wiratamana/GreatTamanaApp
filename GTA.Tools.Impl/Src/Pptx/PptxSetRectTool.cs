using System;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using P = DocumentFormat.OpenXml.Presentation;
using A = DocumentFormat.OpenXml.Drawing;

namespace PptxModifierLib
{
    public static class PptxSetRectTool
    {
        public static PptxOperationResult SetRect(
            byte[] pptxBytes,
            int slideNumber,
            string shapeName,
            double top,
            double left,
            double bottom,
            double right)
        {
            var report = new StringBuilder();
            report.AppendLine("--- pptx_set_rect ---");

            if (pptxBytes == null || pptxBytes.Length == 0)
            {
                report.AppendLine("❌ No presentation bytes supplied.");
                return Fail(report);
            }
            if (string.IsNullOrWhiteSpace(shapeName))
            {
                report.AppendLine("❌ shapeName must not be empty — it identifies which existing object to move/resize.");
                return Fail(report);
            }
            if (right <= left || bottom <= top)
            {
                report.AppendLine("❌ right must be greater than left, and bottom must be greater than top (normalized fractions of the slide's size).");
                return Fail(report);
            }

            try
            {
                string? matchedKind = null;
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

                    matchedKind = ApplyRectToNamedObject(shapeTree, shapeName, offsetXEmu, offsetYEmu, widthEmu, heightEmu);
                    if (matchedKind == null)
                        throw new InvalidOperationException(
                            $"no shape, picture, table/chart, or connector named \"{shapeName}\" was found on slide {slideNumber}.");

                    slidePart.Slide.Save();
                });

                report.AppendLine(
                    $"✅ Moved/resized {matchedKind} \"{shapeName}\" on slide {slideNumber} — normalized edges " +
                    $"top={top:0.####},left={left:0.####},bottom={bottom:0.####},right={right:0.####} -> " +
                    $"EMU pos ({offsetXEmu},{offsetYEmu}), size ({widthEmu}x{heightEmu}).");

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

        private static string? ApplyRectToNamedObject(
            P.ShapeTree shapeTree, string name, long offsetXEmu, long offsetYEmu, long widthEmu, long heightEmu)
        {
            foreach (OpenXmlElement element in shapeTree.ChildElements)
            {
                switch (element)
                {
                    case P.Shape sp
                        when NameMatches(sp.NonVisualShapeProperties?.NonVisualDrawingProperties?.Name?.Value, name):
                        SetShapePropertiesTransform(sp.ShapeProperties ??= new P.ShapeProperties(), offsetXEmu, offsetYEmu, widthEmu, heightEmu);
                        return "shape";

                    case P.Picture pic
                        when NameMatches(pic.NonVisualPictureProperties?.NonVisualDrawingProperties?.Name?.Value, name):
                        SetShapePropertiesTransform(pic.ShapeProperties ??= new P.ShapeProperties(), offsetXEmu, offsetYEmu, widthEmu, heightEmu);
                        return "picture";

                    case P.GraphicFrame gf
                        when NameMatches(gf.NonVisualGraphicFrameProperties?.NonVisualDrawingProperties?.Name?.Value, name):
                        gf.Transform = new P.Transform(
                            new A.Offset { X = offsetXEmu, Y = offsetYEmu },
                            new A.Extents { Cx = widthEmu, Cy = heightEmu });
                        return "table/chart";

                    case P.ConnectionShape cxn
                        when NameMatches(cxn.NonVisualConnectionShapeProperties?.NonVisualDrawingProperties?.Name?.Value, name):
                        SetShapePropertiesTransform(cxn.ShapeProperties ??= new P.ShapeProperties(), offsetXEmu, offsetYEmu, widthEmu, heightEmu);
                        return "connector";
                }
            }
            return null;
        }

        private static bool NameMatches(string? candidate, string name)
            => string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase);

        private static void SetShapePropertiesTransform(
            P.ShapeProperties shapeProps, long offsetXEmu, long offsetYEmu, long widthEmu, long heightEmu)
        {
            A.Transform2D? existing = shapeProps.Transform2D;

            var xfrm = new A.Transform2D(
                new A.Offset { X = offsetXEmu, Y = offsetYEmu },
                new A.Extents { Cx = widthEmu, Cy = heightEmu });

            if (existing?.Rotation?.Value != null)
                xfrm.Rotation = existing.Rotation.Value;
            if (existing?.HorizontalFlip?.Value != null)
                xfrm.HorizontalFlip = existing.HorizontalFlip.Value;
            if (existing?.VerticalFlip?.Value != null)
                xfrm.VerticalFlip = existing.VerticalFlip.Value;

            shapeProps.Transform2D = xfrm;
        }

        /// <summary>Base64 convenience wrapper — mirrors XlsxModifierTool's *FromBase64 pattern.</summary>
        public static PptxOperationResult SetRectFromBase64(
            string base64,
            int slideNumber,
            string shapeName,
            double top,
            double left,
            double bottom,
            double right)
        {
            if (string.IsNullOrWhiteSpace(base64))
                throw new ArgumentException("base64 must not be null or empty.", nameof(base64));
            return SetRect(Convert.FromBase64String(base64), slideNumber, shapeName, top, left, bottom, right);
        }
    }
}
