using System;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using GTA.Tools.Impl.Common;

namespace GTA.Tools
{
    internal static partial class ToolsJiraApi
    {
        private const int JiraReadTextMaxTokens = 100_000;

        [ToolTag("jira_api")]
        [ToolInfo(
            name: "jira_read_text",
            description:
                "Download a plain-text (.txt/.log/etc.) JIRA attachment using the JIRA REST " +
                "API (HTTP Basic Auth with an API token) " +
                "and return its content as plain text. " +
                "Provide either the full attachment 'url' or the numeric 'attachment_id'. " +
                "GUARD: if the estimated token count of the remaining text (from " +
                "'start_char_index' onward) exceeds 100,000, only a chunk that fits the budget " +
                "is returned, prefixed with a note stating the exact char index where loading " +
                "stopped and the 'start_char_index' to pass on the NEXT call to resume reading " +
                "from exactly that point. Keep calling this tool, advancing 'start_char_index' " +
                "each time using the value it reports, until the whole file has been consumed. " +
                "Automatically retries the download (up to 10x) on transient network errors — a " +
                "common symptom of flaky proxy/internet connections. " +
                "Example JIRA ticket with a .txt attachment: " +
                "https://your-company.atlassian.net/browse/PROJ-123")]
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
        [Parameter(index: 3, name: "start_char_index",
            type: "integer",
            description: "0-based character index to start reading from. Default: 0 (start from " +
                         "the beginning of the file). When a previous call returned a 'too big to " +
                         "load entirely' truncation notice, pass the resume index it reported here " +
                         "to continue reading the rest of the file.",
            required: false)]
        [Parameter(index: 4, name: "timeout_ms",
            type: "integer",
            description: "Download timeout in milliseconds. Default: 30000 (30 seconds).",
            required: false)]
        public static object JiraReadText(JsonObject input)
        {
            string? url = ResolveJiraAttachmentUrl(input);
            if (string.IsNullOrWhiteSpace(url))
                return "Error: Provide either 'url' (full attachment URL) or 'attachment_id'.";

            int startCharIndex = input["start_char_index"]?.GetValue<int>() ?? 0;
            if (startCharIndex < 0) startCharIndex = 0;

            int timeoutMs = input["timeout_ms"]?.GetValue<int>() ?? 30_000;

            // Step 1: Download the raw attachment bytes (base64), with retry
            object downloadResult = SendCommandForFileBase64WithRetry(
                new { command = "DownloadImageAsBase64", url, timeoutMs });

            if (downloadResult is string errStr &&
                errStr.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
                return $"❌ jira_read_text — Download failed after " +
                       $"{ToolsCommonPipe.MaxDownloadRetries} attempt(s)\nError: {errStr}";

            string base64 = downloadResult as string ?? string.Empty;
            if (string.IsNullOrEmpty(base64))
                return "❌ jira_read_text — Download returned no data.";

            // Step 2: Decode bytes as text (BOM-aware, defaults to UTF-8)
            string fullText;
            try
            {
                byte[] rawBytes = Convert.FromBase64String(base64);
                using var stream = new MemoryStream(rawBytes);
                using var reader = new StreamReader(stream, Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: true);
                fullText = reader.ReadToEnd();
            }
            catch (Exception ex)
            {
                return $"❌ jira_read_text — Failed to decode attachment as text: {ex.Message}";
            }

            int totalLength = fullText.Length;

            if (totalLength == 0)
                return "The attachment is empty (0 characters).";

            if (startCharIndex >= totalLength)
                return $"start_char_index ({startCharIndex}) is at or beyond the end of the file " +
                       $"(length {totalLength} characters). Nothing more to read — the file has " +
                       $"already been fully retrieved.";

            // Step 3: Token-budget guard
            string remaining = fullText.Substring(startCharIndex);
            int remainingTokensEstimate = ToolsCommonPipe.EstimateTokenCount(remaining);

            // Fits entirely within budget — return the remainder as-is, no wrapper.
            if (remainingTokensEstimate <= JiraReadTextMaxTokens)
                return remaining;

            const int overheadReserveTokens = 200;
            int budget = JiraReadTextMaxTokens - overheadReserveTokens;
            if (budget < 1_000) budget = 1_000; // sanity floor, should never trigger

            int lo = startCharIndex, hi = totalLength;
            while (lo < hi)
            {
                int mid = lo + (hi - lo + 1) / 2;
                int tokens = ToolsCommonPipe.EstimateTokenCount(
                    fullText.Substring(startCharIndex, mid - startCharIndex));
                if (tokens <= budget) lo = mid;
                else hi = mid - 1;
            }

            int endExclusive = lo;
            // Guarantee forward progress even in pathological cases (e.g. a
            // single character somehow estimated above budget).
            if (endExclusive <= startCharIndex)
                endExclusive = Math.Min(startCharIndex + 1, totalLength);

            string chunk          = fullText.Substring(startCharIndex, endExclusive - startCharIndex);
            int    lastCharLoaded = endExclusive - 1;

            return "Its too big to load entirely. " +
                   $"End load {lastCharLoaded} (char index). " +
                   $"File length: {totalLength} characters (~{ToolsCommonPipe.EstimateTokenCount(fullText)} " +
                   $"estimated tokens total). " +
                   $"To continue, call jira_read_text again with start_char_index={endExclusive}.\n" +
                   chunk;
        }
    }
}
