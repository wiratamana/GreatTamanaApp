using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using GTA.PptxLib;
using GTA.Tools.Impl.Common;
using GTA.Tools.Impl.Jira;

namespace GTA.Tools
{
    internal static partial class ToolsJiraApi
    {
        [ToolTag("jira_api")]
        [ToolInfo(
            name: "jira_read_pptx",
            description:
                "Download a PowerPoint (.pptx) JIRA attachment using the JIRA REST API " +
                "(HTTP Basic Auth with an API token), " +
                "extract every slide as a PNG image (rendered " +
                "by PowerPoint COM, pixel-perfect), and feed all slides to the AI as an " +
                "array of base64 image content blocks — one image per slide. " +
                "Provide either the full attachment 'url' or the numeric 'attachment_id'. " +
                "Automatically retries (up to 10×) if the download fails or extraction " +
                "produces zero slides — a common symptom of flaky proxy/internet connections " +
                "corrupting the downloaded file. " +
                "Use from_slide / to_slide to limit the range for large decks. " +
                "AUTOMATIC CHUNKING: Slides are automatically chunked so that total image " +
                "data per response stays within chunk_mb (default 25 MB). " +
                "When a response is chunked, the final content block will be a " +
                "'⚠️ CHUNK BOUNDARY' text block specifying the next from_slide value. " +
                "When you receive a CHUNK BOUNDARY block, immediately call this tool again " +
                "with the indicated from_slide — do NOT answer the user until all chunks " +
                "have been received and every slide has been seen. " +
                "Everything runs in-memory — no file is written to disk.")]
        [Parameter(index: 0, name: "url",
            type: "string",
            description: "Full JIRA attachment content URL. " +
                         "Example: https://your-company.atlassian.net/rest/api/3/attachment/content/12345. " +
                         "Either 'url' or 'attachment_id' is required.",
            required: false)]
        [Parameter(index: 1, name: "attachment_id",
            type: "string",
            description: "JIRA attachment numeric ID. The URL is constructed automatically. " +
                         "Either 'url' or 'attachment_id' is required.",
            required: false)]
        [Parameter(index: 2, name: "jira_base_url",
            type: "string",
            description: "JIRA instance base URL. Only used when 'attachment_id' is provided. " +
                         "Default: the Jira base URL configured in init.ini.",
            required: false)]
        [Parameter(index: 3, name: "from_slide",
            type: "integer",
            description: "First slide to include (1-based). Default: 1.",
            required: false)]
        [Parameter(index: 4, name: "to_slide",
            type: "integer",
            description: "Last slide to include (1-based). Default: all slides.",
            required: false)]
        [Parameter(index: 5, name: "width",
            type: "integer",
            description: "PNG export width in pixels. Default: 1920.",
            required: false)]
        [Parameter(index: 6, name: "height",
            type: "integer",
            description: "PNG export height in pixels. Default: 1080.",
            required: false)]
        [Parameter(index: 7, name: "timeout_ms",
            type: "integer",
            description: "Download timeout in milliseconds. Default: 60000 (1 minute).",
            required: false)]
        [Parameter(index: 8, name: "chunk_mb",
            type: "integer",
            description: "Maximum total PNG image bytes per response, in megabytes. Default: 25. " +
                         "Reduce if the API rejects the request; increase for faster decks.",
            required: false)]
        public static object JiraReadPptx(JsonObject input)
        {
            string? url = ResolveJiraAttachmentUrl(input);
            if (string.IsNullOrWhiteSpace(url))
                return "Error: Provide either 'url' (full attachment URL) or 'attachment_id'.";

            int fromSlide = input["from_slide"]?.GetValue<int>() ?? 1;
            int toSlide   = input["to_slide"]?.GetValue<int>()   ?? int.MaxValue;
            int width     = input["width"]?.GetValue<int>()      ?? 1920;
            int height    = input["height"]?.GetValue<int>()     ?? 1080;
            int timeoutMs = input["timeout_ms"]?.GetValue<int>() ?? 60_000;

            if (fromSlide < 1) fromSlide = 1;
            if (toSlide   < fromSlide) toSlide = fromSlide;

            // Step 1+2: Download PPTX bytes and extract all slides, with retry
            var (success, allSlides, lastError) = ToolsCommonPipe.RetryUntilValid(
                attempt: () => JiraDownloadAndExtractPptx(url!, timeoutMs, width, height),
                isValid: slides => slides != null && slides.Count > 0,
                describeInvalid: slides => "Extraction produced 0 slides " +
                    "(the file may be corrupted/truncated — a valid .pptx always has at least 1 slide).");

            if (!success || allSlides == null || allSlides.Count == 0)
                return $"❌ jira_read_pptx — Failed after {ToolsCommonPipe.MaxDownloadRetries} attempt(s)\n" +
                       $"Error: {lastError}";

            // Step 3: Filter to requested range
            int clampedTo = Math.Min(toSlide, allSlides.Count);
            var slides    = allSlides
                .Where(s => s.SlideNumber >= fromSlide && s.SlideNumber <= clampedTo)
                .ToList();

            if (slides.Count == 0)
                return $"❌ jira_read_pptx — No slides in range [{fromSlide}–{clampedTo}]. " +
                       $"File has {allSlides.Count} slide(s) total.";

            // Step 3b: Apply chunk-size budget
            int  chunkMb     = input["chunk_mb"]?.GetValue<int>() ?? 25;
            long budgetBytes = Math.Max(1L, (long)chunkMb) * 1024L * 1024L;
            var  slidesBatch = new List<SlideResult>();
            long usedBytes   = 0L;

            foreach (SlideResult s in slides)
            {
                long sz = s.PngBytes.Length;
                // Always include at least 1 slide to avoid infinite loops on large single slides.
                if (usedBytes + sz > budgetBytes && slidesBatch.Count > 0) break;
                slidesBatch.Add(s);
                usedBytes += sz;
            }

            bool hasContinuation = slidesBatch.Count < slides.Count;
            int  nextFromSlide   = hasContinuation ? slidesBatch[^1].SlideNumber + 1 : -1;
            int  lastInBatch     = hasContinuation ? slidesBatch[^1].SlideNumber : clampedTo;

            // Step 4: Build JsonArray content blocks for the LLM
            var content = new JsonArray();

            string rangeNote;
            if (hasContinuation)
                rangeNote = $"⚠️ CHUNKED DELIVERY — slides {fromSlide}–{lastInBatch} of " +
                            $"{allSlides.Count} ({usedBytes / 1_048_576.0:F1} MB). " +
                            $"Call again with from_slide={nextFromSlide} for the next chunk.";
            else if (slidesBatch.Count == allSlides.Count)
                rangeNote = $"{allSlides.Count} slide(s) total.";
            else
                rangeNote = $"Showing slides {fromSlide}–{clampedTo} of {allSlides.Count} total. " +
                            $"Use from_slide / to_slide to view other ranges.";

            content.Add(new JsonObject
            {
                ["type"] = "text",
                ["text"] = $"PowerPoint (JIRA attachment) — {rangeNote}"
            });

            foreach (SlideResult slide in slidesBatch)
            {
                content.Add(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = $"--- Slide {slide.SlideNumber} of {allSlides.Count} ---"
                });

                if (slide.PngBytes.Length > 0)
                {
                    content.Add(new JsonObject
                    {
                        ["type"] = "image",
                        ["source"] = new JsonObject
                        {
                            ["type"]       = "base64",
                            ["media_type"] = "image/png",
                            ["data"]       = slide.PngBase64
                        }
                    });
                }
                else
                {
                    content.Add(new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = $"(PNG rendering unavailable for slide {slide.SlideNumber})"
                    });
                }
            }

            if (hasContinuation)
            {
                int remaining = allSlides.Count - nextFromSlide + 1;
                content.Add(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] =
                        $"⚠️ CHUNK BOUNDARY — {remaining} slide(s) remaining " +
                        $"(slides {nextFromSlide}–{allSlides.Count} of {allSlides.Count}).\n" +
                        $"ACTION REQUIRED: Call jira_read_pptx immediately with:\n" +
                        $"  url        = \"{url}\"\n" +
                        $"  from_slide = {nextFromSlide}\n" +
                        $"  chunk_mb   = {chunkMb}\n" +
                        $"Do NOT answer the user until all {allSlides.Count} slides have been received."
                });
            }

            return content;
        }

        private static List<SlideResult> JiraDownloadAndExtractPptx(
            string url, int timeoutMs, int width, int height)
        {
            object downloadResult = SendCommandForFileBase64(
                new { command = "DownloadImageAsBase64", url, timeoutMs });

            if (downloadResult is string err1 &&
                err1.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Download failed — {err1}");

            string pptxBase64 = (string)downloadResult;

            if (string.IsNullOrWhiteSpace(pptxBase64))
                throw new InvalidOperationException(
                    "Download returned empty data (possible corrupted/truncated response).");

            return JiraDocumentExtraction.ExtractPptxSlides(pptxBase64, width, height);
        }
    }
}
