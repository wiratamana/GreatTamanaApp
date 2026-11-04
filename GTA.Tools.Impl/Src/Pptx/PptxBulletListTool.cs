using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml;
using P = DocumentFormat.OpenXml.Presentation;
using A = DocumentFormat.OpenXml.Drawing;

namespace PptxModifierLib
{
    public static class PptxBulletListTool
    {
        private const int MaxIndentLevel = 8;

        private const int IndentStepEmu = 285750;

        public static PptxOperationResult SetBulletList(
            byte[] pptxBytes,
            int slideNumber,
            string shapeName,
            double top,
            double left,
            double bottom,
            double right,
            IEnumerable<(string Text, int IndentLevel)> items,
            bool numbered = false)
        {
            var report = new StringBuilder();
            report.AppendLine("--- pptx_bullet_list ---");

            if (pptxBytes == null || pptxBytes.Length == 0)
            {
                report.AppendLine("❌ No presentation bytes supplied.");
                return Fail(report);
            }
            if (string.IsNullOrWhiteSpace(shapeName))
            {
                report.AppendLine("❌ shapeName must not be empty — it is the stable key used to find/update this list on later calls.");
                return Fail(report);
            }
            if (right <= left || bottom <= top)
            {
                report.AppendLine("❌ right must be greater than left, and bottom must be greater than top (normalized fractions of the slide's size).");
                return Fail(report);
            }

            List<(string Text, int IndentLevel)> itemList = items?.ToList() ?? new List<(string Text, int IndentLevel)>();
            if (itemList.Count == 0)
            {
                report.AppendLine("❌ items must contain at least one (text, indentLevel) entry.");
                return Fail(report);
            }
            foreach (var (itemText, itemLevel) in itemList)
            {
                if (itemText == null)
                {
                    report.AppendLine("❌ item Text must not be null (use \"\" for a blank line).");
                    return Fail(report);
                }
                if (itemLevel < 0 || itemLevel > MaxIndentLevel)
                {
                    report.AppendLine($"❌ item IndentLevel {itemLevel} is out of range — must be between 0 and {MaxIndentLevel}.");
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

                        SetBulletBody(existingShape, itemList, numbered);
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

                        SetBulletBody(newShape, itemList, numbered);

                        shapeTree.Append(newShape);
                        created = true;
                    }

                    slidePart.Slide.Save();
                });

                string kindNote = numbered ? "numbered" : "bulleted";
                report.AppendLine(created
                    ? $"✅ Created {kindNote} list \"{shapeName}\" on slide {slideNumber} — {itemList.Count} item(s), normalized edges top={top:0.####},left={left:0.####},bottom={bottom:0.####},right={right:0.####} -> EMU pos ({offsetXEmu},{offsetYEmu}), size ({widthEmu}x{heightEmu})."
                    : $"✅ Updated {kindNote} list \"{shapeName}\" on slide {slideNumber} — {itemList.Count} item(s), normalized edges top={top:0.####},left={left:0.####},bottom={bottom:0.####},right={right:0.####} -> EMU pos ({offsetXEmu},{offsetYEmu}), size ({widthEmu}x{heightEmu}).");

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

        private static void SetBulletBody(
            P.Shape shape, List<(string Text, int IndentLevel)> items, bool numbered)
        {
            P.TextBody txBody = shape.TextBody ??= new P.TextBody(new A.BodyProperties(), new A.ListStyle());
            txBody.RemoveAllChildren<A.Paragraph>();

            foreach (var (itemText, itemLevel) in items)
            {
                int marL = IndentStepEmu * (itemLevel + 1);
                int indent = -IndentStepEmu;

                var paragraphProps = new A.ParagraphProperties
                {
                    Level = itemLevel,
                    LeftMargin = marL,
                    Indent = indent
                };

                if (numbered)
                {
                    paragraphProps.Append(new A.AutoNumberedBullet { Type = A.TextAutoNumberSchemeValues.ArabicPeriod });
                }
                else
                {
                    paragraphProps.Append(new A.BulletFont
                    {
                        Typeface = "Arial",
                        Panose = "020B0604020202020204",
                        PitchFamily = 34,
                        CharacterSet = 0
                    });
                    paragraphProps.Append(new A.CharacterBullet { Char = "\u2022" });
                }

                var paragraph = new A.Paragraph(paragraphProps);
                if (!string.IsNullOrEmpty(itemText))
                    paragraph.Append(new A.Run(new A.Text(itemText)));

                txBody.Append(paragraph);
            }
        }

        /// <summary>Base64 convenience wrapper — mirrors XlsxModifierTool's *FromBase64 pattern.</summary>
        public static PptxOperationResult SetBulletListFromBase64(
            string base64,
            int slideNumber,
            string shapeName,
            double top,
            double left,
            double bottom,
            double right,
            IEnumerable<(string Text, int IndentLevel)> items,
            bool numbered = false)
        {
            if (string.IsNullOrWhiteSpace(base64))
                throw new ArgumentException("base64 must not be null or empty.", nameof(base64));
            return SetBulletList(Convert.FromBase64String(base64), slideNumber, shapeName,
                top, left, bottom, right, items, numbered);
        }
    }
}
