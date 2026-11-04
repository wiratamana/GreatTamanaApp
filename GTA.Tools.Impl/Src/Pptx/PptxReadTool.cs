using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using GTA.PptxLib;

namespace PptxModifierLib
{
    public sealed class PptxReadResult
    {
        public bool                Success       { get; init; }
        public List<SlideResult>?  Slides        { get; init; }
        public bool                 TimedOut      { get; init; }
        public int                  TimeoutMsUsed { get; init; }
        public string                ReportText    { get; init; } = string.Empty;
    }

    public static class PptxReadTool
    {
        public static PptxReadResult ReadSlides(
            byte[] pptxBytes, int width = 1920, int height = 1080, int timeoutMs = 180_000)
        {
            var report = new StringBuilder();
            report.AppendLine("--- pptx_read ---");

            if (pptxBytes == null || pptxBytes.Length == 0)
            {
                report.AppendLine("❌ No presentation bytes supplied.");
                return new PptxReadResult { Success = false, ReportText = report.ToString().TrimEnd() };
            }

            List<SlideResult> slides          = new();
            Exception?        extractionError = null;

            var thread = new Thread(() =>
            {
                try   { slides = PptxExtractor.ExtractFromBytes(pptxBytes, width, height); }
                catch (Exception ex) { extractionError = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            int  pptxTimeoutMs = Math.Max(timeoutMs, 180_000); // at least 180s — slides take longer
            var  pidsBefore    = GetProcessPids("POWERPNT");
            bool done          = thread.Join(pptxTimeoutMs);

            if (!done)
            {
                KillSpawnedProcesses("POWERPNT", pidsBefore);
                report.AppendLine($"❌ Timed out after {pptxTimeoutMs} ms exporting slides via PowerPoint COM — POWERPNT.EXE was killed.");
                return new PptxReadResult
                {
                    Success       = false,
                    TimedOut      = true,
                    TimeoutMsUsed = pptxTimeoutMs,
                    ReportText    = report.ToString().TrimEnd()
                };
            }

            if (extractionError != null)
            {
                report.AppendLine($"❌ Fatal error: {extractionError.Message}");
                return new PptxReadResult { Success = false, ReportText = report.ToString().TrimEnd() };
            }

            report.AppendLine($"✅ Read {slides.Count} slide(s) — PNG {width}x{height} + slide XML extracted for each.");
            foreach (SlideResult s in slides)
                report.AppendLine($"   • Slide {s.SlideNumber}: PNG {s.PngBytes.Length:N0} bytes, XML {s.XmlContent.Length:N0} chars.");

            return new PptxReadResult { Success = true, Slides = slides, ReportText = report.ToString().TrimEnd() };
        }

        /// <summary>Base64 convenience wrapper — mirrors every other pptx_* tool's *FromBase64 pattern.</summary>
        public static PptxReadResult ReadSlidesFromBase64(
            string base64, int width = 1920, int height = 1080, int timeoutMs = 180_000)
        {
            if (string.IsNullOrWhiteSpace(base64))
                throw new ArgumentException("base64 must not be null or empty.", nameof(base64));
            return ReadSlides(Convert.FromBase64String(base64), width, height, timeoutMs);
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
                    catch {   }
                    finally { p.Dispose(); }
                }
            }
            catch {   }
        }
    }
}
