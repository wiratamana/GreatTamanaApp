using System;
using System.Linq;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using P = DocumentFormat.OpenXml.Presentation;

namespace PptxModifierLib
{
    public static class PptxDeleteSlideTool
    {
        /// <summary>Deletes one existing slide (by its current 1-based visual position) and returns the updated package bytes.</summary>
        public static PptxOperationResult DeleteSlide(byte[] pptxBytes, int position)
        {
            var report = new StringBuilder();
            report.AppendLine("--- pptx_delete_slide ---");

            if (pptxBytes == null || pptxBytes.Length == 0)
            {
                report.AppendLine("❌ No presentation bytes supplied.");
                return new PptxOperationResult { Success = false, ModifiedBytes = null, ReportText = report.ToString().TrimEnd() };
            }

            try
            {
                int remainingCount = 0;

                byte[] result = PptxCommon.EditInMemory(pptxBytes, doc =>
                {
                    PresentationPart presentationPart = doc.PresentationPart
                        ?? throw new InvalidOperationException("this file has no presentation part — is it a valid .pptx?");

                    P.Presentation presentation = presentationPart.Presentation
                        ?? throw new InvalidOperationException("presentation.xml root element is missing.");

                    P.SlideIdList? slideIdList = presentation.SlideIdList;
                    if (slideIdList == null || !slideIdList.Elements<P.SlideId>().Any())
                        throw new InvalidOperationException("this presentation has no slides (no <p:sldIdLst>).");

                    var slideIds = slideIdList.Elements<P.SlideId>().ToList();
                    int totalSlides = slideIds.Count;

                    if (totalSlides <= 1)
                        throw new InvalidOperationException(
                            "cannot delete the only remaining slide — a presentation must keep at least one slide.");
                    if (position < 1 || position > totalSlides)
                        throw new InvalidOperationException(
                            $"position {position} is out of range (presentation has {totalSlides} slide(s)).");

                    P.SlideId targetSlideId = slideIds[position - 1];
                    string? relId = targetSlideId.RelationshipId?.Value;
                    if (string.IsNullOrEmpty(relId))
                        throw new InvalidOperationException("the target <p:sldId> is missing its r:id relationship reference.");

                    if (presentationPart.GetPartById(relId!) is not SlidePart slidePart)
                        throw new InvalidOperationException($"could not resolve the SlidePart for slide at position {position}.");

                    // 1) Drop it from the visual order list.
                    targetSlideId.Remove();
                    presentationPart.Presentation.Save();

                    // 2) Delete the underlying part + its relationship/_rels file.
                    presentationPart.DeletePart(slidePart);

                    remainingCount = totalSlides - 1;
                });

                report.AppendLine($"✅ Slide at position {position} deleted. {remainingCount} slide(s) remain.");
                return new PptxOperationResult { Success = true, ModifiedBytes = result, ReportText = report.ToString().TrimEnd() };
            }
            catch (Exception ex)
            {
                report.AppendLine($"❌ Fatal error: {ex.Message}");
                return new PptxOperationResult { Success = false, ModifiedBytes = null, ReportText = report.ToString().TrimEnd() };
            }
        }

        /// <summary>Base64 convenience wrapper — mirrors XlsxModifierTool's *FromBase64 pattern.</summary>
        public static PptxOperationResult DeleteSlideFromBase64(string base64, int position)
        {
            if (string.IsNullOrWhiteSpace(base64))
                throw new ArgumentException("base64 must not be null or empty.", nameof(base64));
            return DeleteSlide(Convert.FromBase64String(base64), position);
        }
    }
}
