using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using GTA.PptxLib;
using PptxModifierLib;

namespace GTA.Tools
{
    internal static partial class ToolsPptx
    {
        [ToolTag("pptx_local")]
        [ToolInfo(
            name: "pptx_read",
            description:
                "Reads a LOCAL .pptx file, renders every slide to a PNG image (via PowerPoint COM, " +
                "pixel-perfect), and feeds the slides to the AI as an array of base64 image content " +
                "blocks — one image per slide. Use this to visually verify a presentation built/" +
                "edited with the other pptx_* tools. Use from_slide/to_slide to limit the range for " +
                "large decks. AUTOMATIC CHUNKING: slides are automatically chunked so that total " +
                "image data per response stays within chunk_mb (default 25 MB). When a response is " +
                "chunked, the final content block will be a '⚠️ CHUNK BOUNDARY' text block " +
                "specifying the next from_slide value — call this tool again with that value " +
                "immediately, before answering the user.")]
        [Parameter(index: 0, name: "path",
            type: "string",
            description: "Full absolute Windows path to the local .pptx file to read.",
            required: true)]
        [Parameter(index: 1, name: "from_slide",
            type: "integer",
            description: "First slide to include (1-based). Default: 1.",
            required: false)]
        [Parameter(index: 2, name: "to_slide",
            type: "integer",
            description: "Last slide to include (1-based). Default: all slides.",
            required: false)]
        [Parameter(index: 3, name: "width",
            type: "integer",
            description: "PNG export width in pixels. Default: 1920.",
            required: false)]
        [Parameter(index: 4, name: "height",
            type: "integer",
            description: "PNG export height in pixels. Default: 1080.",
            required: false)]
        [Parameter(index: 5, name: "timeout_ms",
            type: "integer",
            description: "Rendering timeout in milliseconds. Floored to 180000 (3 minutes) internally. Default: 180000.",
            required: false)]
        [Parameter(index: 6, name: "chunk_mb",
            type: "integer",
            description: "Maximum total PNG image bytes per response, in megabytes. Default: 25. " +
                         "When the slides in the requested range would exceed this budget, only " +
                         "the slides that fit are returned and a '⚠️ CHUNK BOUNDARY' continuation " +
                         "block is appended with the next from_slide to call.",
            required: false)]
        public static object PptxRead(JsonObject input)
        {
            string? err = ToolsPptxHelpers.LoadLocalPptx(input, out byte[] bytes, out string path);
            if (err != null) return err;

            int fromSlide = ToolsIOHelpers.GetOptionalInt(input["from_slide"]) ?? 1;
            int toSlide = ToolsIOHelpers.GetOptionalInt(input["to_slide"]) ?? int.MaxValue;
            int width = ToolsIOHelpers.GetOptionalInt(input["width"]) ?? 1920;
            int height = ToolsIOHelpers.GetOptionalInt(input["height"]) ?? 1080;
            int timeoutMs = ToolsIOHelpers.GetOptionalInt(input["timeout_ms"]) ?? 180_000;

            if (fromSlide < 1) fromSlide = 1;
            if (toSlide < fromSlide) toSlide = fromSlide;

            PptxReadResult readResult = PptxReadTool.ReadSlides(bytes, width, height, timeoutMs);

            if (readResult.TimedOut)
                return $"❌ pptx_read — PowerPoint COM extraction timed out after " +
                       $"{readResult.TimeoutMsUsed / 1000}s. Try using from_slide/to_slide to " +
                       "process a smaller range.\n\n" + readResult.ReportText;

            if (!readResult.Success || readResult.Slides == null)
                return readResult.ReportText;

            List<SlideResult> allSlides = readResult.Slides;
            if (allSlides.Count == 0)
                return $"❌ pptx_read — No slides found in '{path}'.";

            int clampedTo = Math.Min(toSlide, allSlides.Count);
            var slides = allSlides
                .Where(s => s.SlideNumber >= fromSlide && s.SlideNumber <= clampedTo)
                .ToList();

            if (slides.Count == 0)
                return $"❌ pptx_read — No slides in range [{fromSlide}–{clampedTo}]. " +
                       $"File has {allSlides.Count} slide(s) total.";

            int chunkMb = ToolsIOHelpers.GetOptionalInt(input["chunk_mb"]) ?? 25;
            long budgetBytes = Math.Max(1L, (long)chunkMb) * 1024L * 1024L;
            var slidesBatch = new List<SlideResult>();
            long usedBytes = 0L;

            foreach (SlideResult s in slides)
            {
                long sz = s.PngBytes.Length;
                if (usedBytes + sz > budgetBytes && slidesBatch.Count > 0) break;
                slidesBatch.Add(s);
                usedBytes += sz;
            }

            bool hasContinuation = slidesBatch.Count < slides.Count;
            int nextFromSlide = hasContinuation ? slidesBatch[^1].SlideNumber + 1 : -1;
            int lastInBatch = hasContinuation ? slidesBatch[^1].SlideNumber : clampedTo;

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
                            "Use from_slide/to_slide to view other ranges.";

            content.Add(new JsonObject
            {
                ["type"] = "text",
                ["text"] = $"PowerPoint ({path}) — {rangeNote}"
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
                            ["type"] = "base64",
                            ["media_type"] = "image/png",
                            ["data"] = slide.PngBase64
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
                        "ACTION REQUIRED: Call pptx_read immediately with:\n" +
                        $"  path       = \"{path}\"\n" +
                        $"  from_slide = {nextFromSlide}\n" +
                        $"  chunk_mb   = {chunkMb}\n" +
                        $"Do NOT answer the user until all {allSlides.Count} slides have been received."
                });
            }

            return content;
        }
    }
}
