using System;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using P = DocumentFormat.OpenXml.Presentation;
using A = DocumentFormat.OpenXml.Drawing;

namespace PptxModifierLib
{
    public static class PptxTextBoxTool
    {
        public static PptxOperationResult SetTextBox(
            byte[] pptxBytes,
            int slideNumber,
            string shapeName,
            double top,
            double left,
            double bottom,
            double right,
            string text,
            double fontSizePt = 18,
            bool bold = false,
            bool italic = false,
            string? fontColorHex = null,
            string alignment = "left")
        {
            var report = new StringBuilder();
            report.AppendLine("--- pptx_text_box ---");

            if (pptxBytes == null || pptxBytes.Length == 0)
            {
                report.AppendLine("❌ No presentation bytes supplied.");
                return Fail(report);
            }
            if (string.IsNullOrWhiteSpace(shapeName))
            {
                report.AppendLine("❌ shapeName must not be empty — it is the stable key used to find/update this text box on later calls.");
                return Fail(report);
            }
            if (right <= left || bottom <= top)
            {
                report.AppendLine("❌ right must be greater than left, and bottom must be greater than top (normalized fractions of the slide's size).");
                return Fail(report);
            }
            if (fontSizePt <= 0)
            {
                report.AppendLine("❌ fontSizePt must be positive.");
                return Fail(report);
            }
            if (text == null)
            {
                report.AppendLine("❌ text must not be null.");
                return Fail(report);
            }

            A.TextAlignmentTypeValues alignVal;
            switch ((alignment ?? "left").Trim().ToLowerInvariant())
            {
                case "left": alignVal = A.TextAlignmentTypeValues.Left; break;
                case "center": alignVal = A.TextAlignmentTypeValues.Center; break;
                case "right": alignVal = A.TextAlignmentTypeValues.Right; break;
                default:
                    report.AppendLine($"❌ alignment \"{alignment}\" is not recognized — expected \"left\", \"center\", or \"right\".");
                    return Fail(report);
            }

            string? normalizedColor = null;
            if (fontColorHex != null)
            {
                normalizedColor = PptxCommon.NormalizeHexColor(fontColorHex);
                if (normalizedColor == null)
                {
                    report.AppendLine($"❌ \"{fontColorHex}\" is not a valid hex color (expected e.g. \"FF0000\" or \"#FF0000\").");
                    return Fail(report);
                }
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

                    var xfrm = new A.Transform2D(
                        new A.Offset { X = offsetXEmu, Y = offsetYEmu },
                        new A.Extents { Cx = widthEmu, Cy = heightEmu });

                    P.Shape? existingShape = PptxCommon.FindShapeByName(shapeTree, shapeName);

                    if (existingShape != null)
                    {
                        P.ShapeProperties shapeProps = existingShape.ShapeProperties ??= new P.ShapeProperties();
                        shapeProps.Transform2D = xfrm;

                        SetTextBoxBody(existingShape, text, fontSizePt, bold, italic, normalizedColor, alignVal);

                        created = false;
                    }
                    else
                    {
                        uint newId = PptxCommon.NextShapeId(shapeTree);

                        var newShape = new P.Shape();
                        newShape.NonVisualShapeProperties = new P.NonVisualShapeProperties(
                            new P.NonVisualDrawingProperties { Id = newId, Name = shapeName },
                            new P.NonVisualShapeDrawingProperties { TextBox = true },
                            new P.ApplicationNonVisualDrawingProperties());

                        var shapeProps = new P.ShapeProperties();
                        shapeProps.Append(xfrm);
                        shapeProps.Append(new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle });
                        shapeProps.Append(new A.NoFill());
                        shapeProps.Append(new A.Outline(new A.NoFill()));
                        newShape.ShapeProperties = shapeProps;

                        SetTextBoxBody(newShape, text, fontSizePt, bold, italic, normalizedColor, alignVal);

                        shapeTree.Append(newShape);
                        created = true;
                    }

                    slidePart.Slide.Save();
                });

                string colorNote = normalizedColor != null ? $", font color #{normalizedColor}" : "";
                string styleNote = $"{fontSizePt}pt{(bold ? " bold" : "")}{(italic ? " italic" : "")}{colorNote}, align={alignment}";
                report.AppendLine(created
                    ? $"✅ Created text box \"{shapeName}\" on slide {slideNumber} — normalized edges top={top:0.####},left={left:0.####},bottom={bottom:0.####},right={right:0.####} -> EMU pos ({offsetXEmu},{offsetYEmu}), size ({widthEmu}x{heightEmu}), {styleNote}."
                    : $"✅ Updated text box \"{shapeName}\" on slide {slideNumber} — normalized edges top={top:0.####},left={left:0.####},bottom={bottom:0.####},right={right:0.####} -> EMU pos ({offsetXEmu},{offsetYEmu}), size ({widthEmu}x{heightEmu}), {styleNote}.");

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

        private static void SetTextBoxBody(
            P.Shape shape, string text, double fontSizePt, bool bold, bool italic,
            string? colorHex, A.TextAlignmentTypeValues alignVal)
        {
            P.TextBody txBody = shape.TextBody ??= new P.TextBody(new A.BodyProperties(), new A.ListStyle());
            txBody.RemoveAllChildren<A.Paragraph>();

            int sizeHundredths = (int)Math.Round(fontSizePt * 100);

            var paragraphProps = new A.ParagraphProperties { Alignment = alignVal };
            var paragraph = new A.Paragraph(paragraphProps);

            if (!string.IsNullOrEmpty(text))
            {
                var runProps = new A.RunProperties
                {
                    FontSize = sizeHundredths,
                    Bold = bold,
                    Italic = italic
                };
                if (colorHex != null)
                    runProps.Append(new A.SolidFill(new A.RgbColorModelHex { Val = colorHex }));

                paragraph.Append(new A.Run(runProps, new A.Text(text)));
            }
            else
            {
                // Empty text: still record the intended size so an empty text box
                // doesn't silently fall back to the theme's default run size.
                paragraph.Append(new A.EndParagraphRunProperties { FontSize = sizeHundredths });
            }

            txBody.Append(paragraph);
        }

        /// <summary>Base64 convenience wrapper — mirrors XlsxModifierTool's *FromBase64 pattern.</summary>
        public static PptxOperationResult SetTextBoxFromBase64(
            string base64,
            int slideNumber,
            string shapeName,
            double top,
            double left,
            double bottom,
            double right,
            string text,
            double fontSizePt = 18,
            bool bold = false,
            bool italic = false,
            string? fontColorHex = null,
            string alignment = "left")
        {
            if (string.IsNullOrWhiteSpace(base64))
                throw new ArgumentException("base64 must not be null or empty.", nameof(base64));
            return SetTextBox(Convert.FromBase64String(base64), slideNumber, shapeName,
                top, left, bottom, right, text, fontSizePt, bold, italic, fontColorHex, alignment);
        }
    }
}
