using System;
using System.Linq;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using P = DocumentFormat.OpenXml.Presentation;

namespace PptxModifierLib
{
    public static class PptxNewSlideTool
    {
        private const string MinimalSlideXml = @"<p:sld xmlns:a=""http://schemas.openxmlformats.org/drawingml/2006/main"" xmlns:r=""http://schemas.openxmlformats.org/officeDocument/2006/relationships"" xmlns:p=""http://schemas.openxmlformats.org/presentationml/2006/main"">
  <p:cSld>
    <p:spTree>
      <p:nvGrpSpPr>
        <p:cNvPr id=""1"" name=""""/>
        <p:cNvGrpSpPr/>
        <p:nvPr/>
      </p:nvGrpSpPr>
      <p:grpSpPr>
        <a:xfrm>
          <a:off x=""0"" y=""0""/>
          <a:ext cx=""0"" cy=""0""/>
          <a:chOff x=""0"" y=""0""/>
          <a:chExt cx=""0"" cy=""0""/>
        </a:xfrm>
      </p:grpSpPr>
    </p:spTree>
  </p:cSld>
  <p:clrMapOvr>
    <a:masterClrMapping/>
  </p:clrMapOvr>
</p:sld>";

        /// <summary>Adds one or more new, empty slides to the presentation and returns the updated package bytes.</summary>
        public static PptxOperationResult AddSlide(
            byte[] pptxBytes, string? layoutName = null, int? insertAtPosition = null, int slideCount = 1)
        {
            var report = new StringBuilder();
            report.AppendLine("--- pptx_new_slide ---");

            if (pptxBytes == null || pptxBytes.Length == 0)
            {
                report.AppendLine("❌ No presentation bytes supplied.");
                return new PptxOperationResult { Success = false, ModifiedBytes = null, ReportText = report.ToString().TrimEnd() };
            }

            if (slideCount < 1)
            {
                report.AppendLine($"❌ slideCount must be >= 1 (got {slideCount}).");
                return new PptxOperationResult { Success = false, ModifiedBytes = null, ReportText = report.ToString().TrimEnd() };
            }

            try
            {
                int firstSlidePosition = 0;
                int lastSlidePosition = 0;
                string layoutUsedName = "(unknown)";

                byte[] result = PptxCommon.EditInMemory(pptxBytes, doc =>
                {
                    PresentationPart presentationPart = doc.PresentationPart
                        ?? throw new InvalidOperationException("this file has no presentation part — is it a valid .pptx?");

                    SlideMasterPart? masterPart = presentationPart.SlideMasterParts.FirstOrDefault()
                        ?? throw new InvalidOperationException("this presentation has no slide master to attach a new slide's layout to.");

                    SlideLayoutPart? layoutPart;
                    if (!string.IsNullOrWhiteSpace(layoutName))
                    {
                        layoutPart = masterPart.SlideLayoutParts.FirstOrDefault(lp =>
                            string.Equals(lp.SlideLayout?.CommonSlideData?.Name?.Value, layoutName, StringComparison.OrdinalIgnoreCase));
                        if (layoutPart == null)
                            throw new InvalidOperationException(
                                $"no slide layout named \"{layoutName}\" was found on this presentation's slide master.");
                    }
                    else
                    {
                        layoutPart = masterPart.SlideLayoutParts.FirstOrDefault(lp =>
                                lp.SlideLayout?.Type?.Value == P.SlideLayoutValues.Blank)
                            ?? masterPart.SlideLayoutParts.FirstOrDefault();
                        if (layoutPart == null)
                            throw new InvalidOperationException("this presentation's slide master has no slide layouts at all.");
                    }

                    layoutUsedName = layoutPart.SlideLayout?.CommonSlideData?.Name?.Value ?? "(unnamed layout)";

                    // Register in presentation.xml's <p:sldIdLst>.
                    P.Presentation presentation = presentationPart.Presentation
                        ?? throw new InvalidOperationException("presentation.xml root element is missing.");

                    presentation.SlideIdList ??= new P.SlideIdList();

                    uint maxId = 255; // slide ids must be >= 256
                    foreach (P.SlideId sid in presentation.SlideIdList.Elements<P.SlideId>())
                        if (sid.Id != null && sid.Id.Value > maxId) maxId = sid.Id.Value;

                    int? currentInsertPosition = insertAtPosition;
                    for (int i = 0; i < slideCount; i++)
                    {
                        SlidePart slidePart = presentationPart.AddNewPart<SlidePart>(
                            PptxCommon.NextRelationshipId(presentationPart));
                        slidePart.Slide = new P.Slide(MinimalSlideXml);
                        slidePart.AddPart(layoutPart, PptxCommon.NextRelationshipId(slidePart));
                        slidePart.Slide.Save();

                        maxId += 1;
                        uint newId = maxId;

                        string slideRelId = presentationPart.GetIdOfPart(slidePart);
                        var newSlideIdEl = new P.SlideId { Id = newId, RelationshipId = slideRelId };

                        var existing = presentation.SlideIdList.Elements<P.SlideId>().ToList();
                        int count = existing.Count;
                        int insertIndex = currentInsertPosition.HasValue
                            ? Math.Clamp(currentInsertPosition.Value - 1, 0, count)
                            : count;

                        if (insertIndex >= count)
                            presentation.SlideIdList.Append(newSlideIdEl);
                        else if (insertIndex <= 0)
                            presentation.SlideIdList.InsertAt(newSlideIdEl, 0);
                        else
                            presentation.SlideIdList.InsertAfter(newSlideIdEl, existing[insertIndex - 1]);

                        int thisSlidePosition = insertIndex + 1;
                        if (i == 0) firstSlidePosition = thisSlidePosition;
                        lastSlidePosition = thisSlidePosition;

                        currentInsertPosition = thisSlidePosition + 1;
                    }

                    presentationPart.Presentation.Save();
                });

                report.AppendLine(slideCount == 1
                    ? $"✅ New slide inserted at position {firstSlidePosition}, using layout \"{layoutUsedName}\"."
                    : $"✅ {slideCount} new slides inserted at positions {firstSlidePosition}-{lastSlidePosition}, using layout \"{layoutUsedName}\".");
                return new PptxOperationResult { Success = true, ModifiedBytes = result, ReportText = report.ToString().TrimEnd() };
            }
            catch (Exception ex)
            {
                report.AppendLine($"❌ Fatal error: {ex.Message}");
                return new PptxOperationResult { Success = false, ModifiedBytes = null, ReportText = report.ToString().TrimEnd() };
            }
        }

        /// <summary>Base64 convenience wrapper — mirrors XlsxModifierTool's *FromBase64 pattern.</summary>
        public static PptxOperationResult AddSlideFromBase64(
            string base64, string? layoutName = null, int? insertAtPosition = null, int slideCount = 1)
        {
            if (string.IsNullOrWhiteSpace(base64))
                throw new ArgumentException("base64 must not be null or empty.", nameof(base64));
            return AddSlide(Convert.FromBase64String(base64), layoutName, insertAtPosition, slideCount);
        }
    }
}
