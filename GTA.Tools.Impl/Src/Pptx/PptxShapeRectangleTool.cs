using System;
using System.Linq;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using P = DocumentFormat.OpenXml.Presentation;
using A = DocumentFormat.OpenXml.Drawing;

namespace PptxModifierLib
{
    public static class PptxShapeRectangleTool
    {
        public static PptxOperationResult SetRectangle(
            byte[] pptxBytes,
            int slideNumber,
            string shapeName,
            double top,
            double left,
            double bottom,
            double right,
            string? fillColorHex = null,
            string? text = null,
            double? fontSizePt = null,
            string? horizontalAlign = null,
            string? verticalAlign = null,
            string? fontColorHex = null)
        {
            var report = new StringBuilder();
            report.AppendLine("--- pptx_shape_rectangle ---");

            if (pptxBytes == null || pptxBytes.Length == 0)
            {
                report.AppendLine("❌ No presentation bytes supplied.");
                return Fail(report);
            }
            if (string.IsNullOrWhiteSpace(shapeName))
            {
                report.AppendLine("❌ shapeName must not be empty — it is the stable key used to find/update this rectangle on later calls.");
                return Fail(report);
            }
            if (right <= left || bottom <= top)
            {
                report.AppendLine("❌ right must be greater than left, and bottom must be greater than top (normalized fractions of the slide's size).");
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

                    // Normalized -> EMU translation happens HERE, once, using
                    // THIS presentation's actual slide size.
                    var (slideWidthEmu, slideHeightEmu) = PptxNormalizedCoordinates.GetSlideSizeEmu(presentationPart);
                    (offsetXEmu, offsetYEmu, widthEmu, heightEmu) = PptxNormalizedCoordinates.RectFromEdgesToEmu(
                        top, left, bottom, right, slideWidthEmu, slideHeightEmu);

                    var xfrm = new A.Transform2D(
                        new A.Offset { X = offsetXEmu, Y = offsetYEmu },
                        new A.Extents { Cx = widthEmu, Cy = heightEmu });

                    P.Shape? existingShape = PptxCommon.FindShapeByName(shapeTree, shapeName);

                    if (existingShape != null)
                    {
                        P.ShapeProperties shapeProps = existingShape.ShapeProperties ??= new P.ShapeProperties();
                        shapeProps.Transform2D = xfrm;

                        if (normalizedColor != null)
                            ReplaceFill(shapeProps, normalizedColor);

                        if (text != null)
                            SetShapeText(existingShape, text, fontSizePt, normalizedHorizontalAlign, normalizedVerticalAlign, normalizedFontColor);

                        created = false;
                    }
                    else
                    {
                        uint newId = PptxCommon.NextShapeId(shapeTree);

                        var newShape = new P.Shape();
                        newShape.NonVisualShapeProperties = new P.NonVisualShapeProperties(
                            new P.NonVisualDrawingProperties { Id = newId, Name = shapeName },
                            new P.NonVisualShapeDrawingProperties(),
                            new P.ApplicationNonVisualDrawingProperties());

                        var shapeProps = new P.ShapeProperties();
                        shapeProps.Append(xfrm);
                        shapeProps.Append(new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle });
                        shapeProps.Append(normalizedColor != null
                            ? new A.SolidFill(new A.RgbColorModelHex { Val = normalizedColor })
                            : new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.Accent1 }));
                        newShape.ShapeProperties = shapeProps;

                        var txBody = new P.TextBody(new A.BodyProperties(), new A.ListStyle());
                        newShape.TextBody = txBody;
                        ApplyAutoFit(txBody, normalizedVerticalAlign);
                        var paragraphProps = normalizedHorizontalAlign.HasValue
                            ? new A.ParagraphProperties { Alignment = normalizedHorizontalAlign.Value }
                            : null;
                        var paragraph = paragraphProps != null ? new A.Paragraph(paragraphProps) : new A.Paragraph();
                        if (!string.IsNullOrEmpty(text))
                        {
                            A.RunProperties? runProps = (fontSizePt.HasValue || normalizedFontColor != null)
                                ? new A.RunProperties()
                                : null;
                            if (runProps != null && fontSizePt.HasValue)
                                runProps.FontSize = (int)Math.Round(fontSizePt.Value * 100);
                            if (runProps != null && normalizedFontColor != null)
                                runProps.Append(new A.SolidFill(new A.RgbColorModelHex { Val = normalizedFontColor }));
                            paragraph.Append(runProps != null ? new A.Run(runProps, new A.Text(text)) : new A.Run(new A.Text(text)));
                        }
                        else if (fontSizePt.HasValue || normalizedFontColor != null)
                        {
                            var endProps = new A.EndParagraphRunProperties();
                            if (fontSizePt.HasValue)
                                endProps.FontSize = (int)Math.Round(fontSizePt.Value * 100);
                            if (normalizedFontColor != null)
                                endProps.Append(new A.SolidFill(new A.RgbColorModelHex { Val = normalizedFontColor }));
                            paragraph.Append(endProps);
                        }
                        txBody.Append(paragraph);

                        shapeTree.Append(newShape);
                        created = true;
                    }

                    slidePart.Slide.Save();
                });

                string colorNote = normalizedColor != null ? $", fill #{normalizedColor}" : "";
                string fontNote = fontSizePt.HasValue ? $", font {fontSizePt.Value:0.#}pt" : "";
                string fontColorNote = normalizedFontColor != null ? $", font color #{normalizedFontColor}" : "";
                string alignNote = (horizontalAlign != null || verticalAlign != null)
                    ? $", align ({horizontalAlign ?? "default"}, {verticalAlign ?? "default"})" : "";
                report.AppendLine(created
                    ? $"✅ Created rectangle \"{shapeName}\" on slide {slideNumber} — normalized edges top={top:0.####},left={left:0.####},bottom={bottom:0.####},right={right:0.####} -> EMU pos ({offsetXEmu},{offsetYEmu}), size ({widthEmu}x{heightEmu}){colorNote}{fontNote}{fontColorNote}{alignNote}."
                    : $"✅ Updated rectangle \"{shapeName}\" on slide {slideNumber} — normalized edges top={top:0.####},left={left:0.####},bottom={bottom:0.####},right={right:0.####} -> EMU pos ({offsetXEmu},{offsetYEmu}), size ({widthEmu}x{heightEmu}){colorNote}{fontNote}{fontColorNote}{alignNote}.");

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

        private static void ReplaceFill(P.ShapeProperties shapeProps, string hexColor)
        {
            foreach (var existingFill in shapeProps.Elements()
                         .Where(e => e is A.NoFill or A.SolidFill or A.GradientFill
                                       or A.BlipFill or A.PatternFill or A.GroupFill)
                         .ToList())
            {
                existingFill.Remove();
            }

            var newFill = new A.SolidFill(new A.RgbColorModelHex { Val = hexColor });

            DocumentFormat.OpenXml.OpenXmlElement? geometryElement =
                (DocumentFormat.OpenXml.OpenXmlElement?)shapeProps.GetFirstChild<A.PresetGeometry>()
                ?? shapeProps.GetFirstChild<A.CustomGeometry>();

            if (geometryElement != null)
                shapeProps.InsertAfter(newFill, geometryElement);
            else
                shapeProps.Append(newFill);
        }

        private static void SetShapeText(
            P.Shape shape, string text, double? fontSizePt = null,
            A.TextAlignmentTypeValues? horizontalAlign = null,
            A.TextAnchoringTypeValues? verticalAlign = null,
            string? fontColorHex = null)
        {
            P.TextBody txBody = shape.TextBody ??= new P.TextBody(new A.BodyProperties(), new A.ListStyle());
            txBody.RemoveAllChildren<A.Paragraph>();
            ApplyAutoFit(txBody, verticalAlign);

            var paragraphProps = horizontalAlign.HasValue
                ? new A.ParagraphProperties { Alignment = horizontalAlign.Value }
                : null;
            var paragraph = paragraphProps != null ? new A.Paragraph(paragraphProps) : new A.Paragraph();
            if (!string.IsNullOrEmpty(text))
            {
                A.RunProperties? runProps = (fontSizePt.HasValue || fontColorHex != null)
                    ? new A.RunProperties()
                    : null;
                if (runProps != null && fontSizePt.HasValue)
                    runProps.FontSize = (int)Math.Round(fontSizePt.Value * 100);
                if (runProps != null && fontColorHex != null)
                    runProps.Append(new A.SolidFill(new A.RgbColorModelHex { Val = fontColorHex }));
                paragraph.Append(runProps != null ? new A.Run(runProps, new A.Text(text)) : new A.Run(new A.Text(text)));
            }
            else if (fontSizePt.HasValue || fontColorHex != null)
            {
                var endProps = new A.EndParagraphRunProperties();
                if (fontSizePt.HasValue)
                    endProps.FontSize = (int)Math.Round(fontSizePt.Value * 100);
                if (fontColorHex != null)
                    endProps.Append(new A.SolidFill(new A.RgbColorModelHex { Val = fontColorHex }));
                paragraph.Append(endProps);
            }
            txBody.Append(paragraph);
        }

        private static void ApplyAutoFit(P.TextBody txBody, A.TextAnchoringTypeValues? verticalAlign = null)
        {
            A.BodyProperties bodyProps = txBody.GetFirstChild<A.BodyProperties>()
                ?? throw new InvalidOperationException("TextBody is missing its required a:bodyPr element.");
            foreach (var existingAutoFit in bodyProps.Elements()
                         .Where(e => e is A.NoAutoFit or A.NormalAutoFit or A.ShapeAutoFit)
                         .ToList())
            {
                existingAutoFit.Remove();
            }
            bodyProps.AppendChild(new A.NormalAutoFit());

            if (verticalAlign.HasValue)
                bodyProps.Anchor = verticalAlign.Value;
        }

        /// <summary>Base64 convenience wrapper — mirrors XlsxModifierTool's *FromBase64 pattern.</summary>
        public static PptxOperationResult SetRectangleFromBase64(
            string base64,
            int slideNumber,
            string shapeName,
            double top,
            double left,
            double bottom,
            double right,
            string? fillColorHex = null,
            string? text = null,
            double? fontSizePt = null,
            string? horizontalAlign = null,
            string? verticalAlign = null,
            string? fontColorHex = null)
        {
            if (string.IsNullOrWhiteSpace(base64))
                throw new ArgumentException("base64 must not be null or empty.", nameof(base64));
            return SetRectangle(Convert.FromBase64String(base64), slideNumber, shapeName,
                top, left, bottom, right, fillColorHex, text, fontSizePt, horizontalAlign, verticalAlign, fontColorHex);
        }
    }
}
