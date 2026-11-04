using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using GTA.DocxLib;
using GTA.PdfLib;
using GTA.PptxLib;
using GTA.XlsxLib;

namespace GTA.Tools.Impl.Box
{
    public static class BoxDocumentExtraction
    {

        public readonly struct PptxExtractionResult
        {
            public List<SlideResult>? Slides   { get; init; }
            public bool               TimedOut { get; init; }
            public int                TimeoutMsUsed { get; init; }
            public string?            Error    { get; init; }
        }

        public static PptxExtractionResult ExtractSlides(string pptxBase64, int width, int height, int timeoutMs)
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

            int  pptxTimeoutMs = Math.Max(timeoutMs, 180_000); // at least 180s (slides take longer)
            var  pidsBefore    = GetProcessPids("POWERPNT");
            bool done          = thread.Join(pptxTimeoutMs);

            if (!done)
            {
                KillSpawnedProcesses("POWERPNT", pidsBefore);
                return new PptxExtractionResult { TimedOut = true, TimeoutMsUsed = pptxTimeoutMs };
            }

            if (extractionError != null)
                return new PptxExtractionResult { Error = extractionError.Message };

            return new PptxExtractionResult { Slides = allSlides };
        }

        public readonly struct XlsxXmlExtractionResult
        {
            public string?  XmlContent    { get; init; }
            public bool     TimedOut      { get; init; }
            public int      TimeoutMsUsed { get; init; }
            public string?  Error         { get; init; }
        }

        public static XlsxXmlExtractionResult ExtractXmlOnly(string xlsxBase64, int timeoutMs)
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

            int  xlsxTimeoutMs = Math.Max(timeoutMs, 120_000); // at least 120s
            var  pidsBefore    = GetProcessPids("EXCEL");
            bool done          = thread.Join(xlsxTimeoutMs);

            if (!done)
            {
                KillSpawnedProcesses("EXCEL", pidsBefore);
                return new XlsxXmlExtractionResult { TimedOut = true, TimeoutMsUsed = xlsxTimeoutMs };
            }

            if (extractionError != null)
                return new XlsxXmlExtractionResult { Error = extractionError.Message };

            return new XlsxXmlExtractionResult { XmlContent = xmlContent };
        }

        public static List<XlsxImageResult> ExtractImagesOnly(string xlsxBase64)
            => XlsxExtractorTool.ExtractImagesOnlyFromBase64(xlsxBase64);

        public static DocxExtractionResult ExtractDocx(string docxBase64, string label)
            => DocxExtractorTool.ExtractFromBase64(docxBase64, label);

        public readonly struct PdfExtractionResult
        {
            public List<PdfPageResult>? Pages { get; init; }
            public string?               Error { get; init; }
        }

        public static PdfExtractionResult ExtractPdfPages(string pdfBase64, int dpi)
        {
            try
            {
                List<PdfPageResult> pages = PdfExtractor.ExtractFromBase64(pdfBase64, dpi);
                return new PdfExtractionResult { Pages = pages };
            }
            catch (Exception ex)
            {
                return new PdfExtractionResult { Error = ex.Message };
            }
        }

        private static HashSet<int> GetProcessPids(string processName)
        {
            try
            {
                return new HashSet<int>(
                    Process.GetProcessesByName(processName).Select(p => p.Id));
            }
            catch { return new HashSet<int>(); }
        }

        private static void KillSpawnedProcesses(string processName, HashSet<int> pidsBefore)
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(processName))
                {
                    if (pidsBefore.Contains(p.Id)) continue; // was already running — leave it
                    try { p.Kill(entireProcessTree: true); }
                    catch { }
                    finally { p.Dispose(); }
                }
            }
            catch { }
        }
    }
}
