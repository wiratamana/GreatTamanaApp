using System;
using System.IO;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using P = DocumentFormat.OpenXml.Presentation;
using A = DocumentFormat.OpenXml.Drawing;

namespace PptxModifierLib
{
    public static class PptxImageTool
    {
        public static PptxOperationResult SetImage(
            byte[] pptxBytes,
            int slideNumber,
            string shapeName,
            byte[] imageBytes,
            string imageFormat,
            double top,
            double left,
            double bottom,
            double right)
        {
            var report = new StringBuilder();
            report.AppendLine("--- pptx_image ---");

            if (pptxBytes == null || pptxBytes.Length == 0)
            {
                report.AppendLine("❌ No presentation bytes supplied.");
                return Fail(report);
            }
            if (string.IsNullOrWhiteSpace(shapeName))
            {
                report.AppendLine("❌ shapeName must not be empty — it is the stable key used to find/update this picture on later calls.");
                return Fail(report);
            }
            if (imageBytes == null || imageBytes.Length == 0)
            {
                report.AppendLine("❌ imageBytes must not be null or empty.");
                return Fail(report);
            }
            if (right <= left || bottom <= top)
            {
                report.AppendLine("❌ right must be greater than left, and bottom must be greater than top (normalized fractions of the slide's size).");
                return Fail(report);
            }

            PartTypeInfo partType;
            switch ((imageFormat ?? "").Trim().ToLowerInvariant())
            {
                case "png": partType = ImagePartType.Png; break;
                case "jpeg":
                case "jpg": partType = ImagePartType.Jpeg; break;
                default:
                    report.AppendLine($"❌ imageFormat \"{imageFormat}\" is not recognized — expected \"png\" or \"jpeg\".");
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

                    var (slideWidthEmu, slideHeightEmu) = PptxNormalizedCoordinates.GetSlideSizeEmu(presentationPart);
                    (offsetXEmu, offsetYEmu, widthEmu, heightEmu) = PptxNormalizedCoordinates.RectFromEdgesToEmu(
                        top, left, bottom, right, slideWidthEmu, slideHeightEmu);

                    var xfrm = new A.Transform2D(
                        new A.Offset { X = offsetXEmu, Y = offsetYEmu },
                        new A.Extents { Cx = widthEmu, Cy = heightEmu });

                    string newRelId = PptxCommon.NextRelationshipId(slidePart);
                    ImagePart imagePart = slidePart.AddImagePart(partType, newRelId);
                    using (var ms = new MemoryStream(imageBytes))
                        imagePart.FeedData(ms);

                    P.Picture? existingPicture = FindPictureByName(shapeTree, shapeName);

                    if (existingPicture != null)
                    {
                        string? oldRelId = existingPicture.BlipFill?.Blip?.Embed?.Value;

                        existingPicture.BlipFill ??= new P.BlipFill();
                        existingPicture.BlipFill.RemoveAllChildren<A.Blip>();
                        existingPicture.BlipFill.RemoveAllChildren<A.Stretch>();
                        existingPicture.BlipFill.RemoveAllChildren<A.Tile>();
                        existingPicture.BlipFill.PrependChild(new A.Stretch(new A.FillRectangle()));
                        existingPicture.BlipFill.PrependChild(new A.Blip { Embed = newRelId });

                        P.ShapeProperties shapeProps = existingPicture.ShapeProperties ??= new P.ShapeProperties();
                        shapeProps.Transform2D = xfrm;

                        if (!string.IsNullOrEmpty(oldRelId) && !string.Equals(oldRelId, newRelId, StringComparison.OrdinalIgnoreCase)
                            && slidePart.GetPartById(oldRelId!) is ImagePart oldImagePart)
                        {
                            slidePart.DeletePart(oldImagePart);
                        }

                        created = false;
                    }
                    else
                    {
                        uint newId = PptxCommon.NextShapeId(shapeTree);

                        var newPicture = new P.Picture(
                            new P.NonVisualPictureProperties(
                                new P.NonVisualDrawingProperties { Id = newId, Name = shapeName },
                                new P.NonVisualPictureDrawingProperties(new A.PictureLocks { NoChangeAspect = true }),
                                new P.ApplicationNonVisualDrawingProperties()),
                            new P.BlipFill(
                                new A.Blip { Embed = newRelId },
                                new A.Stretch(new A.FillRectangle())),
                            new P.ShapeProperties(
                                xfrm,
                                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }));

                        shapeTree.Append(newPicture);
                        created = true;
                    }

                    slidePart.Slide.Save();
                });

                report.AppendLine(created
                    ? $"✅ Created picture \"{shapeName}\" ({imageFormat}, {imageBytes.Length:N0} bytes) on slide {slideNumber} — normalized edges top={top:0.####},left={left:0.####},bottom={bottom:0.####},right={right:0.####} -> EMU pos ({offsetXEmu},{offsetYEmu}), size ({widthEmu}x{heightEmu})."
                    : $"✅ Updated picture \"{shapeName}\" ({imageFormat}, {imageBytes.Length:N0} bytes) on slide {slideNumber} — normalized edges top={top:0.####},left={left:0.####},bottom={bottom:0.####},right={right:0.####} -> EMU pos ({offsetXEmu},{offsetYEmu}), size ({widthEmu}x{heightEmu}).");

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

        private static P.Picture? FindPictureByName(P.ShapeTree shapeTree, string name)
        {
            foreach (P.Picture picture in shapeTree.Elements<P.Picture>())
            {
                string? n = picture.NonVisualPictureProperties?.NonVisualDrawingProperties?.Name?.Value;
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
                    return picture;
            }
            return null;
        }

        /// <summary>Base64 convenience wrapper — mirrors XlsxModifierTool's *FromBase64 pattern.</summary>
        public static PptxOperationResult SetImageFromBase64(
            string base64,
            int slideNumber,
            string shapeName,
            byte[] imageBytes,
            string imageFormat,
            double top,
            double left,
            double bottom,
            double right)
        {
            if (string.IsNullOrWhiteSpace(base64))
                throw new ArgumentException("base64 must not be null or empty.", nameof(base64));
            return SetImage(Convert.FromBase64String(base64), slideNumber, shapeName,
                imageBytes, imageFormat, top, left, bottom, right);
        }
    }
}
