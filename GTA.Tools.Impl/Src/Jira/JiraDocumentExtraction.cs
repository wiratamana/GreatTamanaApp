using System;
using System.Collections.Generic;
using System.Threading;
using GTA.DocxLib;
using GTA.PptxLib;
using GTA.XlsxLib;

namespace GTA.Tools.Impl.Jira
{
    public static class JiraDocumentExtraction
    {
        public static List<SlideResult> ExtractPptxSlides(string pptxBase64, int width, int height)
        {
            List<SlideResult> allSlides      = new();
            Exception?        extractionError = null;

            var thread = new Thread(() =>
            {
                try   { allSlides = PptxExtractor.ExtractFromBase64(pptxBase64, width, height); }
                catch (Exception ex) { extractionError = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (extractionError != null)
                throw new InvalidOperationException(
                    $"Extraction failed — the file may be corrupted/truncated: {extractionError.Message}",
                    extractionError);

            return allSlides;
        }

        public static string ExtractXlsxXmlOnly(string xlsxBase64)
        {
            string?    xmlContent      = null;
            Exception? extractionError = null;

            var thread = new Thread(() =>
            {
                try   { xmlContent = XlsxExtractorTool.ExtractXmlOnlyFromBase64(xlsxBase64); }
                catch (Exception ex) { extractionError = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (extractionError != null)
                throw new InvalidOperationException(
                    $"Extraction failed — the file may be corrupted/truncated: {extractionError.Message}",
                    extractionError);

            if (string.IsNullOrWhiteSpace(xmlContent))
                throw new InvalidOperationException(
                    "Extraction succeeded but produced no XML content (file may be corrupted/truncated).");

            return xmlContent;
        }

        public static List<XlsxImageResult> ExtractXlsxImages(string xlsxBase64)
            => XlsxExtractorTool.ExtractImagesOnlyFromBase64(xlsxBase64);

        public static DocxExtractionResult ExtractDocx(string docxBase64)
            => DocxExtractorTool.ExtractFromBase64(docxBase64);
    }
}
