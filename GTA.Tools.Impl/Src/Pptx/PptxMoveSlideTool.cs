using System;
using System.Linq;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using P = DocumentFormat.OpenXml.Presentation;

namespace PptxModifierLib
{
    public static class PptxMoveSlideTool
    {
        public static PptxOperationResult MoveSlide(byte[] pptxBytes, int fromPosition, int toPosition)
        {
            var report = new StringBuilder();
            report.AppendLine("--- pptx_move_slide ---");

            if (pptxBytes == null || pptxBytes.Length == 0)
            {
                report.AppendLine("❌ No presentation bytes supplied.");
                return new PptxOperationResult { Success = false, ModifiedBytes = null, ReportText = report.ToString().TrimEnd() };
            }

            try
            {
                int totalSlides = 0;

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
                    totalSlides = slideIds.Count;

                    if (fromPosition < 1 || fromPosition > totalSlides)
                        throw new InvalidOperationException(
                            $"fromPosition {fromPosition} is out of range (presentation has {totalSlides} slide(s)).");
                    if (toPosition < 1 || toPosition > totalSlides)
                        throw new InvalidOperationException(
                            $"toPosition {toPosition} is out of range (presentation has {totalSlides} slide(s)).");

                    if (fromPosition == toPosition)
                        return; // no-op — already in the requested position.

                    P.SlideId moving = slideIds[fromPosition - 1];
                    moving.Remove();

                    var remaining = slideIdList.Elements<P.SlideId>().ToList();
                    int insertIndex = toPosition - 1;

                    if (insertIndex >= remaining.Count)
                        slideIdList.Append(moving);
                    else if (insertIndex <= 0)
                        slideIdList.InsertAt(moving, 0);
                    else
                        slideIdList.InsertAfter(moving, remaining[insertIndex - 1]);

                    presentationPart.Presentation.Save();
                });

                report.AppendLine(fromPosition == toPosition
                    ? $"✅ Slide already at position {toPosition} — no move needed ({totalSlides} slide(s) total)."
                    : $"✅ Slide moved from position {fromPosition} to position {toPosition} ({totalSlides} slide(s) total).");
                return new PptxOperationResult { Success = true, ModifiedBytes = result, ReportText = report.ToString().TrimEnd() };
            }
            catch (Exception ex)
            {
                report.AppendLine($"❌ Fatal error: {ex.Message}");
                return new PptxOperationResult { Success = false, ModifiedBytes = null, ReportText = report.ToString().TrimEnd() };
            }
        }

        /// <summary>Base64 convenience wrapper — mirrors XlsxModifierTool's *FromBase64 pattern.</summary>
        public static PptxOperationResult MoveSlideFromBase64(string base64, int fromPosition, int toPosition)
        {
            if (string.IsNullOrWhiteSpace(base64))
                throw new ArgumentException("base64 must not be null or empty.", nameof(base64));
            return MoveSlide(Convert.FromBase64String(base64), fromPosition, toPosition);
        }
    }
}
