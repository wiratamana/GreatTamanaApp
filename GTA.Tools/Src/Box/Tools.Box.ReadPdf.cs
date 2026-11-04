using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using GTA.Tools.Impl.Box;
using GTA.PdfLib;

namespace GTA.Tools
{
    internal static partial class ToolsBoxApi
    {
        [ToolTag("box_api")]
        [ToolInfo(
            name: "box_read_pdf",
            description:
                "Download a PDF file from Box using a pure-HTTP authenticated Box session, " +
                "render every PAGE as its own PNG image (via PDFtoImage/PDFium — no Adobe/Word/" +
                "PowerPoint required), and feed all pages to the AI as an array of base64 image " +
                "content blocks — ONE IMAGE PER PAGE, never a single stitched image. This matters " +
                "most for 'slide-deck style' PDFs (a PPTX exported/printed to PDF, one slide per " +
                "page) — each page/slide is delivered as its own crisp image instead of being " +
                "crushed into one absurdly tall image. Everything runs in-memory — no file is " +
                "written to disk. " +
                "SMART FILE IDENTIFICATION: pass EITHER a numeric Box file_id, OR a shared-link " +
                "token/URL (via file_id or the dedicated shared_link_token parameter) — the tool " +
                "auto-detects which kind of identifier it received. Use shared_link_token when " +
                "your account can view/download the file only via its shared link (no direct " +
                "folder/file permission) — this downloads the file using the shared-link's own " +
                "access grant instead of the account's folder permissions, which fixes 403s that " +
                "occur even when the file is clearly openable via its shared link in a browser. " +
                "Use from_page / to_page to limit the range for large PDFs. " +
                "AUTOMATIC CHUNKING: Pages are automatically chunked so that total image " +
                "data per response stays within chunk_mb (default 25 MB). " +
                "When a response is chunked, the final content block will be a " +
                "'⚠️ CHUNK BOUNDARY' text block specifying the next from_page value. " +
                "When you receive a CHUNK BOUNDARY block, immediately call this tool again " +
                "with the indicated from_page — do NOT answer the user until all chunks " +
                "have been received and every page has been seen.")]
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
        [Parameter(index: 1, name: "from_page",
            type: "integer",
            description: "First page to include (1-based). Default: 1.",
            required: false)]
        [Parameter(index: 2, name: "to_page",
            type: "integer",
            description: "Last page to include (1-based). Default: all pages.",
            required: false)]
        [Parameter(index: 3, name: "dpi",
            type: "integer",
            description: "Rasterization DPI for every rendered page. Higher = sharper but bigger " +
                         "PNGs (and slower). Default: 150. Try 100–120 for very large page counts " +
                         "to keep chunk_mb budgets from truncating too aggressively; try 200+ for " +
                         "dense text/small-print pages that need to stay legible.",
            required: false)]
        [Parameter(index: 4, name: "timeout_ms",
            type: "integer",
            description: "Download timeout in milliseconds. Default: 120000 (2 minutes).",
            required: false)]
        [Parameter(index: 5, name: "chunk_mb",
            type: "integer",
            description: "Maximum total PNG image bytes per response, in megabytes. Default: 25. " +
                         "When the pages in the requested range would exceed this budget, only " +
                         "the pages that fit are returned and a '⚠️ CHUNK BOUNDARY' continuation " +
                         "block is appended with the next from_page to call. Reduce if Azure " +
                         "still rejects; increase on fast connections to fetch more pages per call.",
            required: false)]
        [Parameter(index: 6, name: "shared_link_url",
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
        [Parameter(index: 7, name: "shared_link_token",
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
        public static object BoxReadPdf(JsonObject input)
        {
            string? rawFileId          = input["file_id"]?.GetValue<string>();
            string? rawSharedLinkToken = input["shared_link_token"]?.GetValue<string>();
            string? sharedLinkUrl      = input["shared_link_url"]?.GetValue<string>(); // legacy priming flow

            int     fromPage      = input["from_page"]?.GetValue<int>()  ?? 1;
            int     toPage        = input["to_page"]?.GetValue<int>()    ?? int.MaxValue;
            int     dpi           = input["dpi"]?.GetValue<int>()        ?? 150;
            int     timeoutMs     = input["timeout_ms"]?.GetValue<int>() ?? 120_000;

            if (fromPage < 1) fromPage = 1;
            if (toPage   < fromPage) toPage = fromPage;
            if (dpi < 30) dpi = 30; // guard against pathological/zero DPI values

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

            // Step 1: Download PDF bytes from Box in-memory
            string? pdfBase64;
            string? downloadError;
            string  effectiveFileIdLabel;

            if (sharedToken != null)
            {
                var (b64, resolvedFileId, err) =
                    BoxApiClient.FetchBase64ViaSharedLink(sharedToken, numericFileId, timeoutMs);

                pdfBase64            = b64;
                downloadError        = err;
                effectiveFileIdLabel = resolvedFileId ?? numericFileId ?? $"(shared token {sharedToken})";
            }
            else
            {
                var (b64, err) = BoxApiClient.FetchBase64(numericFileId!, timeoutMs, sharedLinkUrl);
                pdfBase64            = b64;
                downloadError        = err;
                effectiveFileIdLabel = numericFileId!;
            }

            if (!string.IsNullOrEmpty(downloadError))
                return $"❌ box_read_pdf — Download failed\nError: {downloadError}";

            // Step 2: Extract pages via GTA.PdfLib (no COM, no STA thread needed)
            var extraction = BoxDocumentExtraction.ExtractPdfPages(pdfBase64!, dpi);

            if (extraction.Error != null)
                return $"❌ box_read_pdf — Extraction failed\nError: {extraction.Error}";

            List<PdfPageResult> allPages = extraction.Pages ?? new List<PdfPageResult>();
            if (allPages.Count == 0)
                return "❌ box_read_pdf — No pages found in the PDF file.";

            // Step 3: Filter to requested range
            int clampedTo = Math.Min(toPage, allPages.Count);
            var pages     = allPages
                .Where(p => p.PageNumber >= fromPage && p.PageNumber <= clampedTo)
                .ToList();

            if (pages.Count == 0)
                return $"❌ box_read_pdf — No pages in range [{fromPage}–{clampedTo}]. " +
                       $"File has {allPages.Count} page(s) total.";

            // Step 3b: Apply chunk-size budget
            int  chunkMb     = input["chunk_mb"]?.GetValue<int>() ?? 25;
            long budgetBytes = Math.Max(1L, (long)chunkMb) * 1024L * 1024L;
            var  pagesBatch  = new List<PdfPageResult>();
            long usedBytes   = 0L;

            foreach (PdfPageResult p in pages)
            {
                long sz = p.PngBytes.Length;
                // Always include at least 1 page even if it alone exceeds budget,
                // otherwise we'd loop forever on very large single pages.
                if (usedBytes + sz > budgetBytes && pagesBatch.Count > 0) break;
                pagesBatch.Add(p);
                usedBytes += sz;
            }

            bool hasContinuation = pagesBatch.Count < pages.Count;
            int  nextFromPage    = hasContinuation ? pagesBatch[^1].PageNumber + 1 : -1;
            int  lastInBatch     = hasContinuation ? pagesBatch[^1].PageNumber : clampedTo;

            // Step 4: Build JsonArray content blocks for the LLM
            var content = new JsonArray();

            string rangeNote;
            if (hasContinuation)
                rangeNote = $"⚠️ CHUNKED DELIVERY — pages {fromPage}–{lastInBatch} of " +
                            $"{allPages.Count} ({usedBytes / 1_048_576.0:F1} MB). " +
                            $"Call again with from_page={nextFromPage} for the next chunk.";
            else if (pagesBatch.Count == allPages.Count)
                rangeNote = $"{allPages.Count} page(s) total.";
            else
                rangeNote = $"Showing pages {fromPage}–{clampedTo} of {allPages.Count} total. " +
                            $"Use from_page / to_page to view other ranges.";

            content.Add(new JsonObject
            {
                ["type"] = "text",
                ["text"] = $"PDF (Box file {effectiveFileIdLabel}) — {rangeNote}"
            });

            foreach (PdfPageResult page in pagesBatch)
            {
                content.Add(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = $"--- Page {page.PageNumber} of {allPages.Count} ---"
                });

                if (page.PngBytes.Length > 0)
                {
                    content.Add(new JsonObject
                    {
                        ["type"] = "image",
                        ["source"] = new JsonObject
                        {
                            ["type"]       = "base64",
                            ["media_type"] = "image/png",
                            ["data"]       = page.PngBase64
                        }
                    });
                }
                else
                {
                    content.Add(new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = $"(PNG rendering unavailable for page {page.PageNumber})"
                    });
                }
            }

            // Step 4b: Append CHUNK BOUNDARY block when truncated
            if (hasContinuation)
            {
                int remaining = allPages.Count - nextFromPage + 1;

                string continuationIdParam = numericFileId != null
                    ? $"  file_id   = \"{numericFileId}\""
                    : $"  file_id   = \"{effectiveFileIdLabel}\"\n  shared_link_token = \"{sharedToken}\"";

                content.Add(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] =
                        $"⚠️ CHUNK BOUNDARY — {remaining} page(s) remaining " +
                        $"(pages {nextFromPage}–{allPages.Count} of {allPages.Count}).\n" +
                        $"ACTION REQUIRED: Call box_read_pdf immediately with:\n" +
                        $"{continuationIdParam}\n" +
                        $"  from_page = {nextFromPage}\n" +
                        $"  dpi       = {dpi}\n" +
                        $"  chunk_mb  = {chunkMb}\n" +
                        $"Do NOT answer the user until all {allPages.Count} pages have been received."
                });
            }

            return content;
        }
    }
}
