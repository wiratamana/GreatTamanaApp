using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using GTA.Tools.Impl.Box;
using GTA.PptxLib;

namespace GTA.Tools
{
    internal static partial class ToolsBoxApi
    {
        [ToolTag("box_api")]
        [ToolInfo(
            name: "box_read_pptx",
            description:
                "Download a PowerPoint (.pptx) file from Box using a pure-HTTP authenticated Box " +
                "session, extract every slide as a PNG image (rendered by PowerPoint " +
                "COM, pixel-perfect), and feed all slides to the AI as an array of base64 image " +
                "content blocks — one image per slide. Everything runs in-memory — no file is " +
                "written to disk. " +
                "SMART FILE IDENTIFICATION: pass EITHER a numeric Box file_id, OR a shared-link " +
                "token/URL (via file_id or the dedicated shared_link_token parameter) — the tool " +
                "auto-detects which kind of identifier it received. Use shared_link_token when " +
                "your account can view/download the file only via its shared link (no direct " +
                "folder/file permission) — this downloads the file using the shared-link's own " +
                "access grant instead of the account's folder permissions, which fixes 403s that " +
                "occur even when the file is clearly openable via its shared link in a browser. " +
                "AUTOMATIC CHUNKING: Slides are automatically chunked so that total image " +
                "data per response stays within chunk_mb (default 25 MB). " +
                "When a response is chunked, the final content block will be a " +
                "'⚠️ CHUNK BOUNDARY' text block specifying the next from_slide value. " +
                "When you receive a CHUNK BOUNDARY block, immediately call this tool again " +
                "with the indicated from_slide — do NOT answer the user until all chunks " +
                "have been received and every slide has been seen.")]
        [Parameter(index: 0, name: "file_id",
            type: "string",
            description: "Numeric Box file ID without the 'f_' prefix (e.g. '1234567890123'), OR " +
                         "a Box shared-link token/URL (e.g. 'abcd1234efgh5678ijkl9012mnop3456' or " +
                         "'https://<tenant>.box.com/s/abcd1234efgh5678ijkl9012mnop3456'). The tool " +
                         "auto-detects which kind of value this is (all-digits => file ID, " +
                         "otherwise => shared-link token/URL). Optional if shared_link_token is " +
                         "provided instead; if BOTH are provided, this numeric file_id is used " +
                         "directly (more reliable — skips auto-detecting the file ID from the " +
                         "shared-link page).",
            required: false)]
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
            description: "Download timeout in milliseconds. Default: 120000 (2 minutes).",
            required: false)]
        [Parameter(index: 6, name: "chunk_mb",
            type: "integer",
            description: "Maximum total PNG image bytes per response, in megabytes. Default: 25. " +
                         "When the slides in the requested range would exceed this budget, only " +
                         "the slides that fit are returned and a '⚠️ CHUNK BOUNDARY' continuation " +
                         "block is appended with the next from_slide to call. Reduce if Azure " +
                         "still rejects; increase on fast connections to fetch more slides per call.",
            required: false)]
        [Parameter(index: 7, name: "shared_link_url",
            type: "string",
            description: "LEGACY / rarely needed now that shared_link_token exists. OPTIONAL fix " +
                         "for 403 Forbidden errors when file_id was discovered via a Box shared " +
                         "link but the account ALSO has direct folder access. Pass the original " +
                         "shared link URL here and the tool will first issue an authenticated GET to it " +
                         "to register the shared-item access grant before downloading by file_id. " +
                         "If your account has NO direct folder/file access at all, use " +
                         "shared_link_token instead — it downloads via the shared link's own " +
                         "grant rather than relying on folder permissions.",
            required: false)]
        [Parameter(index: 8, name: "shared_link_token",
            type: "string",
            description: "Box shared-link token or full shared-link URL, e.g. " +
                         "'abcd1234efgh5678ijkl9012mnop3456' or " +
                         "'https://<tenant>.box.com/s/abcd1234efgh5678ijkl9012mnop3456'. Use this " +
                         "when your account can only view/download the file only via its shared link " +
                         "(no direct folder/file permission) — 403s occur when downloading purely " +
                         "by file_id in that situation. The tool navigates to the shared link and " +
                         "downloads the file using that page's own access grant/session, instead " +
                         "of the account's folder permissions. Pairing this with a numeric file_id " +
                         "(if known) is more reliable than relying on auto-detection of the file " +
                         "ID from the shared-link page.",
            required: false)]
        public static object BoxReadPptx(JsonObject input)
        {
            string? rawFileId          = input["file_id"]?.GetValue<string>();
            string? rawSharedLinkToken = input["shared_link_token"]?.GetValue<string>();
            string? sharedLinkUrl      = input["shared_link_url"]?.GetValue<string>(); // legacy priming flow

            int     fromSlide     = input["from_slide"]?.GetValue<int>() ?? 1;
            int     toSlide       = input["to_slide"]?.GetValue<int>()   ?? int.MaxValue;
            int     width         = input["width"]?.GetValue<int>()      ?? 1920;
            int     height        = input["height"]?.GetValue<int>()     ?? 1080;
            int     timeoutMs     = input["timeout_ms"]?.GetValue<int>() ?? 120_000;

            if (fromSlide < 1) fromSlide = 1;
            if (toSlide   < fromSlide) toSlide = fromSlide;

            // Step 0: Smart-detect file_id vs. shared-link token
            string? numericFileId = null;
            string? sharedToken   = null;

            if (!string.IsNullOrWhiteSpace(rawSharedLinkToken))
                sharedToken = BoxApiClient.ExtractSharedToken(rawSharedLinkToken);

            if (!string.IsNullOrWhiteSpace(rawFileId))
            {
                if (BoxApiClient.LooksLikeNumericFileId(rawFileId))
                    numericFileId = rawFileId.Trim();
                else
                    sharedToken ??= BoxApiClient.ExtractSharedToken(rawFileId);
            }

            if (numericFileId == null && sharedToken == null)
                return "Error: provide either 'file_id' (numeric) or 'shared_link_token' " +
                       "(shared-link token/URL).";

            // Step 1: Download PPTX bytes from Box in-memory
            string? pptxBase64;
            string? downloadError;
            string  effectiveFileIdLabel;

            if (sharedToken != null)
            {
                var (b64, resolvedFileId, err) =
                    BoxApiClient.FetchBase64ViaSharedLink(sharedToken, numericFileId, timeoutMs);

                pptxBase64            = b64;
                downloadError         = err;
                effectiveFileIdLabel  = resolvedFileId ?? numericFileId ?? $"(shared token {sharedToken})";
            }
            else
            {
                var (b64, err) = BoxApiClient.FetchBase64(numericFileId!, timeoutMs, sharedLinkUrl);
                pptxBase64           = b64;
                downloadError        = err;
                effectiveFileIdLabel = numericFileId!;
            }

            if (!string.IsNullOrEmpty(downloadError))
                return $"❌ box_read_pptx — Download failed\nError: {downloadError}";

            // Step 2: Extract slides via GTA.PptxLib (STA + timeout owned by Impl)
            var extraction = BoxDocumentExtraction.ExtractSlides(pptxBase64!, width, height, timeoutMs);

            if (extraction.TimedOut)
            {
                return
                    $"❌ box_read_pptx — PowerPoint COM extraction timed out after {extraction.TimeoutMsUsed / 1000}s.\n" +
                    $"Box file   : {effectiveFileIdLabel}\n" +
                    "The spawned POWERPNT.EXE process has been killed.\n" +
                    "Try using from_slide/to_slide to process a smaller range.";
            }

            if (extraction.Error != null)
                return $"❌ box_read_pptx — Extraction failed\nError: {extraction.Error}";

            List<SlideResult> allSlides = extraction.Slides ?? new List<SlideResult>();
            if (allSlides.Count == 0)
                return "❌ box_read_pptx — No slides found in the PPTX file.";

            // Step 3: Filter to requested range
            int clampedTo = Math.Min(toSlide, allSlides.Count);
            var slides    = allSlides
                .Where(s => s.SlideNumber >= fromSlide && s.SlideNumber <= clampedTo)
                .ToList();

            if (slides.Count == 0)
                return $"❌ box_read_pptx — No slides in range [{fromSlide}–{clampedTo}]. " +
                       $"File has {allSlides.Count} slide(s) total.";

            // Step 3b: Apply chunk-size budget
            int  chunkMb      = input["chunk_mb"]?.GetValue<int>() ?? 25;
            long budgetBytes  = Math.Max(1L, (long)chunkMb) * 1024L * 1024L;
            var  slidesBatch  = new List<SlideResult>();
            long usedBytes    = 0L;

            foreach (SlideResult s in slides)
            {
                long sz = s.PngBytes.Length;
                // Always include at least 1 slide even if it alone exceeds budget,
                // otherwise we'd loop forever on very large single slides.
                if (usedBytes + sz > budgetBytes && slidesBatch.Count > 0) break;
                slidesBatch.Add(s);
                usedBytes += sz;
            }

            bool hasContinuation   = slidesBatch.Count < slides.Count;
            int  nextFromSlide     = hasContinuation ? slidesBatch[^1].SlideNumber + 1 : -1;
            int  lastInBatch       = hasContinuation ? slidesBatch[^1].SlideNumber : clampedTo;

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
                ["text"] = $"PowerPoint (Box file {effectiveFileIdLabel}) — {rangeNote}"
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

            // Step 4b: Append CHUNK BOUNDARY block when truncated
            if (hasContinuation)
            {
                int remaining = allSlides.Count - nextFromSlide + 1;

                string continuationIdParam = numericFileId != null
                    ? $"  file_id   = \"{numericFileId}\""
                    : $"  file_id   = \"{effectiveFileIdLabel}\"\n  shared_link_token = \"{sharedToken}\"";

                content.Add(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] =
                        $"⚠️ CHUNK BOUNDARY — {remaining} slide(s) remaining " +
                        $"(slides {nextFromSlide}–{allSlides.Count} of {allSlides.Count}).\n" +
                        $"ACTION REQUIRED: Call box_read_pptx immediately with:\n" +
                        $"{continuationIdParam}\n" +
                        $"  from_slide = {nextFromSlide}\n" +
                        $"  chunk_mb   = {chunkMb}\n" +
                        $"Do NOT answer the user until all {allSlides.Count} slides have been received."
                });
            }

            return content;
        }
    }
}
