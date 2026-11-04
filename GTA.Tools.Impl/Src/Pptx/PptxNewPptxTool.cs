using System;
using System;
using System.IO;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using P = DocumentFormat.OpenXml.Presentation;
using A = DocumentFormat.OpenXml.Drawing;

namespace PptxModifierLib
{
    public static class PptxNewPptxTool
    {
        private const string ThemeXml = @"<a:theme xmlns:a=""http://schemas.openxmlformats.org/drawingml/2006/main"" name=""Office Theme"">
  <a:themeElements>
    <a:clrScheme name=""Office"">
      <a:dk1><a:sysClr val=""windowText"" lastClr=""000000""/></a:dk1>
      <a:lt1><a:sysClr val=""window"" lastClr=""FFFFFF""/></a:lt1>
      <a:dk2><a:srgbClr val=""44546A""/></a:dk2>
      <a:lt2><a:srgbClr val=""E7E6E6""/></a:lt2>
      <a:accent1><a:srgbClr val=""4472C4""/></a:accent1>
      <a:accent2><a:srgbClr val=""ED7D31""/></a:accent2>
      <a:accent3><a:srgbClr val=""A5A5A5""/></a:accent3>
      <a:accent4><a:srgbClr val=""FFC000""/></a:accent4>
      <a:accent5><a:srgbClr val=""5B9BD5""/></a:accent5>
      <a:accent6><a:srgbClr val=""70AD47""/></a:accent6>
      <a:hlink><a:srgbClr val=""0563C1""/></a:hlink>
      <a:folHlink><a:srgbClr val=""954F72""/></a:folHlink>
    </a:clrScheme>
    <a:fontScheme name=""Office"">
      <a:majorFont>
        <a:latin typeface=""Calibri Light""/>
        <a:ea typeface=""""/>
        <a:cs typeface=""""/>
      </a:majorFont>
      <a:minorFont>
        <a:latin typeface=""Calibri""/>
        <a:ea typeface=""""/>
        <a:cs typeface=""""/>
      </a:minorFont>
    </a:fontScheme>
    <a:fmtScheme name=""Office"">
      <a:fillStyleLst>
        <a:solidFill><a:schemeClr val=""phClr""/></a:solidFill>
        <a:solidFill><a:schemeClr val=""phClr""/></a:solidFill>
        <a:solidFill><a:schemeClr val=""phClr""/></a:solidFill>
      </a:fillStyleLst>
      <a:lnStyleLst>
        <a:ln w=""6350"" cap=""flat"" cmpd=""sng"" algn=""ctr""><a:solidFill><a:schemeClr val=""phClr""/></a:solidFill><a:prstDash val=""solid""/></a:ln>
        <a:ln w=""12700"" cap=""flat"" cmpd=""sng"" algn=""ctr""><a:solidFill><a:schemeClr val=""phClr""/></a:solidFill><a:prstDash val=""solid""/></a:ln>
        <a:ln w=""19050"" cap=""flat"" cmpd=""sng"" algn=""ctr""><a:solidFill><a:schemeClr val=""phClr""/></a:solidFill><a:prstDash val=""solid""/></a:ln>
      </a:lnStyleLst>
      <a:effectStyleLst>
        <a:effectStyle><a:effectLst/></a:effectStyle>
        <a:effectStyle><a:effectLst/></a:effectStyle>
        <a:effectStyle><a:effectLst/></a:effectStyle>
      </a:effectStyleLst>
      <a:bgFillStyleLst>
        <a:solidFill><a:schemeClr val=""phClr""/></a:solidFill>
        <a:solidFill><a:schemeClr val=""phClr""/></a:solidFill>
        <a:solidFill><a:schemeClr val=""phClr""/></a:solidFill>
      </a:bgFillStyleLst>
    </a:fmtScheme>
  </a:themeElements>
</a:theme>";

        private const string BlankSlideLayoutXml = @"<p:sldLayout xmlns:a=""http://schemas.openxmlformats.org/drawingml/2006/main"" xmlns:r=""http://schemas.openxmlformats.org/officeDocument/2006/relationships"" xmlns:p=""http://schemas.openxmlformats.org/presentationml/2006/main"" type=""blank"" preserve=""1"">
  <p:cSld name=""Blank"">
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
</p:sldLayout>";

        private const string SlideMasterBodyXml = @"<p:sldMaster xmlns:a=""http://schemas.openxmlformats.org/drawingml/2006/main"" xmlns:r=""http://schemas.openxmlformats.org/officeDocument/2006/relationships"" xmlns:p=""http://schemas.openxmlformats.org/presentationml/2006/main"">
  <p:cSld>
    <p:bg>
      <p:bgRef idx=""1001"">
        <a:schemeClr val=""bg1""/>
      </p:bgRef>
    </p:bg>
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
  <p:clrMap bg1=""lt1"" tx1=""dk1"" bg2=""lt2"" tx2=""dk2"" accent1=""accent1"" accent2=""accent2"" accent3=""accent3"" accent4=""accent4"" accent5=""accent5"" accent6=""accent6"" hlink=""hlink"" folHlink=""folHlink""/>
  <p:txStyles>
    <p:titleStyle/>
    <p:bodyStyle/>
    <p:otherStyle/>
  </p:txStyles>
</p:sldMaster>";

        private static P.Slide CreateRealBlankSlide()
        {
            return new P.Slide(
                new P.CommonSlideData(
                    new P.ShapeTree(
                        new P.NonVisualGroupShapeProperties(
                            new P.NonVisualDrawingProperties { Id = 1U, Name = string.Empty },
                            new P.NonVisualGroupShapeDrawingProperties(),
                            new P.ApplicationNonVisualDrawingProperties()),
                        new P.GroupShapeProperties(
                            new A.TransformGroup(
                                new A.Offset { X = 0L, Y = 0L },
                                new A.Extents { Cx = 0L, Cy = 0L },
                                new A.ChildOffset { X = 0L, Y = 0L },
                                new A.ChildExtents { Cx = 0L, Cy = 0L })))),
                new P.ColorMapOverride(new A.MasterColorMapping()));
        }

        /// <summary>Builds a brand-new, PowerPoint-openable .pptx package in memory and returns its raw bytes.</summary>
        public static byte[] CreateBlankPresentation(
            long slideWidthEmu = PptxCommon.WidescreenWidthEmu,
            long slideHeightEmu = PptxCommon.WidescreenHeightEmu)
        {
            if (slideWidthEmu <= 0 || slideHeightEmu <= 0)
                throw new ArgumentException("slideWidthEmu/slideHeightEmu must both be positive EMU values.");

            using var ms = new MemoryStream();
            using (PresentationDocument doc = PresentationDocument.Create(ms, PresentationDocumentType.Presentation))
            {
                PresentationPart presentationPart = doc.AddPresentationPart();

                // Keep explicit rIdN-style relationship ids so the SDK writes
                // normal relative relationship targets.
                string themeRelId = PptxCommon.NextRelationshipId(presentationPart);
                ThemePart themePart = presentationPart.AddNewPart<ThemePart>(themeRelId);
                themePart.Theme = new A.Theme(ThemeXml);
                themePart.Theme.Save();

                string presToMasterRelId = PptxCommon.NextRelationshipId(presentationPart);
                SlideMasterPart masterPart = presentationPart.AddNewPart<SlideMasterPart>(presToMasterRelId);

                string masterToThemeRelId = PptxCommon.NextRelationshipId(masterPart);
                masterPart.AddPart(themePart, masterToThemeRelId);

                // Master -> Layout ("Blank").
                string layoutRelId = PptxCommon.NextRelationshipId(masterPart);
                SlideLayoutPart layoutPart = masterPart.AddNewPart<SlideLayoutPart>(layoutRelId);
                layoutPart.SlideLayout = new P.SlideLayout(BlankSlideLayoutXml);
                layoutPart.SlideLayout.Save();

                string layoutToMasterRelId = PptxCommon.NextRelationshipId(layoutPart);
                layoutPart.AddPart(masterPart, layoutToMasterRelId);

                var master = new P.SlideMaster(SlideMasterBodyXml);
                P.ColorMap? clrMap = master.GetFirstChild<P.ColorMap>();
                var layoutIdList = new P.SlideLayoutIdList(
                    new P.SlideLayoutId { Id = 2147483649U, RelationshipId = layoutRelId });
                if (clrMap != null)
                    master.InsertAfter(layoutIdList, clrMap);
                else
                    master.Append(layoutIdList);

                masterPart.SlideMaster = master;
                masterPart.SlideMaster.Save();

                // Create ONE real slide. A no-slide shell is too minimal for
                // PowerPoint and triggers the repair dialog.
                string slideRelId = PptxCommon.NextRelationshipId(presentationPart);
                SlidePart slidePart = presentationPart.AddNewPart<SlidePart>(slideRelId);

                string slideToLayoutRelId = PptxCommon.NextRelationshipId(slidePart);
                slidePart.AddPart(layoutPart, slideToLayoutRelId);
                slidePart.Slide = CreateRealBlankSlide();
                slidePart.Slide.Save();

                var presentation = new P.Presentation
                {
                    SlideMasterIdList = new P.SlideMasterIdList(
                        new P.SlideMasterId { Id = 2147483648U, RelationshipId = presToMasterRelId }),
                    SlideIdList = new P.SlideIdList(
                        new P.SlideId { Id = 256U, RelationshipId = slideRelId }),
                    SlideSize = new P.SlideSize
                    {
                        Cx = (Int32Value)(int)slideWidthEmu,
                        Cy = (Int32Value)(int)slideHeightEmu
                    },
                    NotesSize = new P.NotesSize { Cx = 6858000, Cy = 9144000 },
                    DefaultTextStyle = new P.DefaultTextStyle()
                };

                presentationPart.Presentation = presentation;
                presentationPart.Presentation.Save();

                doc.Save();
            }

            return ms.ToArray();
        }

        /// <summary>Report-wrapped convenience overload — same shape as every other pptx_*/xlsx_* modifying tool's return value.</summary>
        public static PptxOperationResult CreateBlankPresentationResult(
            long slideWidthEmu = PptxCommon.WidescreenWidthEmu,
            long slideHeightEmu = PptxCommon.WidescreenHeightEmu)
        {
            try
            {
                byte[] bytes = CreateBlankPresentation(slideWidthEmu, slideHeightEmu);
                string report =
                    "--- pptx_new_pptx ---\n" +
                    $"✅ Created blank presentation — {slideWidthEmu} x {slideHeightEmu} EMU " +
                    $"({PptxCommon.EmuToInches(slideWidthEmu):0.###}in x {PptxCommon.EmuToInches(slideHeightEmu):0.###}in), " +
                    $"{bytes.Length} bytes, 1 blank slide (master + \"Blank\" layout + theme ready).";
                return new PptxOperationResult { Success = true, ModifiedBytes = bytes, ReportText = report };
            }
            catch (Exception ex)
            {
                return new PptxOperationResult
                {
                    Success = false,
                    ModifiedBytes = null,
                    ReportText = $"--- pptx_new_pptx ---\n❌ Fatal error: {ex.Message}"
                };
            }
        }

        /// <summary>Base64 convenience wrapper — mirrors XlsxModifierTool's *FromBase64 pattern.</summary>
        public static string CreateBlankPresentationBase64(
            long slideWidthEmu = PptxCommon.WidescreenWidthEmu,
            long slideHeightEmu = PptxCommon.WidescreenHeightEmu)
            => Convert.ToBase64String(CreateBlankPresentation(slideWidthEmu, slideHeightEmu));
    }
}
