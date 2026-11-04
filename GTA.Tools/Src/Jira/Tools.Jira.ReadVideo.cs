using System;
using System.IO;
using System.Text.Json.Nodes;
using GTA.Tools.Impl.Common;

namespace GTA.Tools
{
    internal static partial class ToolsJiraApi
    {
        private const int JiraVideoOuterRetries = 3;

        [ToolTag("jira_api")]
        [ToolInfo(
            name: "jira_read_video",
            description:
                "Download a video JIRA attachment (e.g. a bug-repro screen recording, .mp4/.webm/.mov) " +
                "using the JIRA REST API (HTTP Basic Auth with an API token), then use Google Gemini " +
                "(gemini-3.1-pro-preview — the highest-quality Gemini model) to WATCH the entire " +
                "video and return a complete, structured, timestamped analysis as text. " +
                "The attachment is first downloaded to a temporary local file (Gemini cannot " +
                "authenticate to JIRA directly — its API only accepts Google File API URIs or " +
                "YouTube URLs), then uploaded to the Gemini File API for analysis; the local temp " +
                "file is always DELETED afterwards, whether the analysis succeeds or fails — " +
                "nothing is left on disk. Download is automatically retried if it fails or " +
                "returns an implausibly small/corrupted payload (a common symptom of flaky " +
                "proxy/internet connections). " +
                "By default the tool asks Gemini for an EXHAUSTIVE report covering: an overall " +
                "summary; the application/environment shown; a chronological timeline with " +
                "timestamps for every distinct screen/scene change; every user action (click, " +
                "type, scroll, drag, hover, key press) with the exact UI element and value " +
                "involved; all on-screen text transcribed verbatim (labels, dialogs, tooltips, " +
                "and especially error/warning messages); all audio content (narration, system " +
                "sounds) transcribed or described; explicit callouts of any bugs, glitches, " +
                "crashes, or unexpected UI behaviour with timestamps; and a final conclusion. " +
                "Pass a custom 'prompt' to ask a narrower or different question instead of the " +
                "default exhaustive analysis. " +
                "Provide either the full attachment 'url' (from jira_read_ticket attachment list) " +
                "or the numeric 'attachment_id'. " +
                "This is a long-running synchronous call — video upload + Gemini indexing can take " +
                "roughly 1-3 minutes for a typical bug-repro clip, longer for large/long videos.")]
        [Parameter(index: 0, name: "url",
            type: "string",
            description: "Full JIRA attachment content URL. " +
                         "Example: https://your-company.atlassian.net/rest/api/3/attachment/content/12345. " +
                         "Obtain this from the 'URL' field in jira_read_ticket attachment list. " +
                         "Either 'url' or 'attachment_id' is required.",
            required: false)]
        [Parameter(index: 1, name: "attachment_id",
            type: "string",
            description: "JIRA attachment numeric ID (shown in brackets in jira_read_ticket output). " +
                         "The URL is constructed as {jira_base_url}/rest/api/3/attachment/content/{attachment_id}. " +
                         "Either 'url' or 'attachment_id' is required.",
            required: false)]
        [Parameter(index: 2, name: "jira_base_url",
            type: "string",
            description: "JIRA instance base URL. Only used when 'attachment_id' is provided. " +
                         "Default: the Jira base URL configured in init.ini.",
            required: false)]
        [Parameter(index: 3, name: "prompt",
            type: "string",
            description: "Custom question/instruction to ask Gemini about the video, overriding the " +
                         "built-in exhaustive-analysis prompt. Use this to focus on a specific aspect, " +
                         "e.g. \"Does the validation error appear before or after the submit button is " +
                         "clicked? Quote the exact error text and its timestamp.\"",
            required: false)]
        [Parameter(index: 4, name: "mime_type",
            type: "string",
            description: "Video MIME type of the attachment. Default: 'video/mp4'. Change only if the " +
                         "attachment is a non-MP4 video format, e.g. 'video/webm' or 'video/quicktime'.",
            required: false)]
        [Parameter(index: 5, name: "download_timeout_ms",
            type: "integer",
            description: "Timeout in milliseconds for downloading the attachment from JIRA. " +
                         "Default: 120000 (2 minutes). Increase for very large video files.",
            required: false)]
        [Parameter(index: 6, name: "max_wait_ms",
            type: "integer",
            description: "Maximum time in milliseconds to wait for Gemini to finish processing " +
                         "(indexing) the uploaded video before giving up. Default: 300000 (5 minutes). " +
                         "Increase for long videos.",
            required: false)]
        [Parameter(index: 7, name: "gemini_api_key",
            type: "string",
            description: "Gemini API key override. Defaults to the pre-configured project key — " +
                         "only provide this to use a different Gemini account/quota.",
            required: false)]
        public static object JiraReadVideo(JsonObject input)
        {
            string? url = ResolveJiraAttachmentUrl(input);
            if (string.IsNullOrWhiteSpace(url))
                return "Error: Provide either 'url' (full attachment URL) or 'attachment_id'.";

            string  mimeType     = input["mime_type"]?.GetValue<string>() ?? "video/mp4";
            int     dlTimeoutMs  = input["download_timeout_ms"]?.GetValue<int>() ?? 120_000;
            int     maxWaitMs    = input["max_wait_ms"]?.GetValue<int>()         ?? 300_000;
            string  apiKey       = input["gemini_api_key"]?.GetValue<string>()   ?? GeminiVideoHelper.DefaultApiKey;
            string? customPrompt = input["prompt"]?.GetValue<string>();

            string prompt = string.IsNullOrWhiteSpace(customPrompt) ? JiraVideoDefaultPrompt : customPrompt;

            // Step 1: Download video bytes, with an outer validity-guard retry
            var (success, videoBytes, lastError) = ToolsCommonPipe.RetryUntilValid(
                attempt: () => JiraDownloadVideoBytes(url!, dlTimeoutMs),
                isValid: b => b != null && b.Length > 1024,
                describeInvalid: b => $"Downloaded video data looked too small/corrupted " +
                    $"({(b?.Length ?? 0)} byte(s)) — expected a real video file.",
                maxRetries: JiraVideoOuterRetries);

            if (!success || videoBytes == null)
                return $"❌ jira_read_video — Download failed after {JiraVideoOuterRetries} attempt(s)\n" +
                       $"Error: {lastError}";

            // Step 2: Persist to a temp local file — Gemini upload needs a real path
            string ext = mimeType.ToLowerInvariant() switch
            {
                "video/webm"      => ".webm",
                "video/quicktime" => ".mov",
                "video/x-msvideo" => ".avi",
                "video/x-matroska"=> ".mkv",
                _                 => ".mp4"
            };
            string tempDir  = Path.Combine(Path.GetTempPath(), "JiraVideoTemp");
            string tempFile = Path.Combine(tempDir, $"jira_video_{Guid.NewGuid():N}{ext}");

            try
            {
                Directory.CreateDirectory(tempDir);
                File.WriteAllBytes(tempFile, videoBytes);
            }
            catch (Exception ex)
            {
                return $"❌ jira_read_video — Failed to save the downloaded video locally\nError: {ex.Message}";
            }

            // Step 3-5: Gemini File API upload → wait ACTIVE → ask Pro model
            string        sizeInfo = $"{new FileInfo(tempFile).Length / 1_048_576.0:F1} MB";
            System.Text.StringBuilder diagLog = new System.Text.StringBuilder();

            try
            {
                var (fileUri, fileName) = GeminiVideoHelper.UploadAsync(
                    tempFile, mimeType, apiKey,
                    s => diagLog.AppendLine(s)).GetAwaiter().GetResult();

                GeminiVideoHelper.WaitForActiveAsync(
                    fileName, apiKey,
                    maxWaitMs,
                    s => diagLog.AppendLine(s)).GetAwaiter().GetResult();

                string? answer = GeminiVideoHelper.AskAsync(
                    fileUri, mimeType, prompt, GeminiVideoHelper.ProModel, apiKey,
                    s => diagLog.AppendLine(s)).GetAwaiter().GetResult();

                if (string.IsNullOrWhiteSpace(answer))
                    return $"⚠️ jira_read_video — Gemini returned an empty response for '{url}' " +
                           $"({sizeInfo}).\n\nDiagnostic log:\n{diagLog}";

                return "╔══════════════════════════════════════════════════╗\n" +
                       "  jira_read_video — Gemini Pro video analysis\n" +
                       $"  Model  : {GeminiVideoHelper.ProModel}\n" +
                       $"  Source : {url}\n" +
                       $"  Size   : {sizeInfo}\n" +
                       "╚══════════════════════════════════════════════════╝\n\n" +
                       answer;
            }
            catch (Exception ex)
            {
                return $"❌ jira_read_video — Gemini video analysis failed\n" +
                       $"Error: {ex.Message}\n\nDiagnostic log:\n{diagLog}";
            }
            finally
            {
                // Step 6: ALWAYS delete the local video file before returning
                try { if (File.Exists(tempFile)) File.Delete(tempFile); }
                catch { /* best-effort cleanup — never let this mask the real result */ }
            }
        }

        private static byte[] JiraDownloadVideoBytes(string url, int dlTimeoutMs)
        {
            object downloadResult = SendCommandForFileBase64WithRetry(
                new { command = "DownloadImageAsBase64", url, timeoutMs = dlTimeoutMs });

            if (downloadResult is string err1 &&
                err1.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(err1);

            string videoBase64 = (string)downloadResult;

            if (string.IsNullOrWhiteSpace(videoBase64))
                throw new InvalidOperationException("Download returned empty data.");

            return Convert.FromBase64String(videoBase64);
        }

        private const string JiraVideoDefaultPrompt =
            "You are analyzing a screen-recording video attached to a JIRA ticket (typically a bug " +
            "report or a QA repro/regression clip). Watch the ENTIRE video carefully, including " +
            "audio, and produce a complete, structured report with the following sections:\n\n" +
            "1. SUMMARY — 2-4 sentences describing what the video demonstrates overall and its apparent purpose.\n" +
            "2. ENVIRONMENT — Application/website name, page/screen titles, browser or OS chrome visible, " +
            "and any version/build numbers shown.\n" +
            "3. CHRONOLOGICAL TIMELINE — For EVERY distinct screen, scene, or state change, give an " +
            "approximate timestamp (mm:ss) and describe what is shown.\n" +
            "4. USER ACTIONS — List every user action in order (click, double-click, type, scroll, drag, " +
            "hover, key press, tab switch, copy/paste) with a timestamp, the exact UI element clicked or " +
            "typed into (button label, field name, menu item, link text), and the value typed if applicable.\n" +
            "5. ON-SCREEN TEXT — Transcribe verbatim any important on-screen text: labels, form field " +
            "values, tooltips, dialog/modal contents, notifications/toasts, and especially any ERROR " +
            "MESSAGES or WARNING banners (quote them exactly, with timestamp).\n" +
            "6. AUDIO — Transcribe or describe any narration, spoken commentary, system sounds, or " +
            "notification sounds, with timestamps.\n" +
            "7. BUGS / UNEXPECTED BEHAVIOUR — Explicitly call out anything that looks like a bug, glitch, " +
            "crash, visual defect, incorrect data, unexpected navigation, or behaviour that seems " +
            "inconsistent with a normal working application. For each, give the timestamp, what was " +
            "expected, and what actually happened.\n" +
            "8. CONCLUSION — Your best assessment of the root cause or nature of the issue being " +
            "demonstrated (if a bug), or confirmation that the workflow completed successfully (if not).\n\n" +
            "Be exhaustive and specific — prefer concrete timestamps, exact button/field names, and exact " +
            "error text over vague generalizations. Do not skip minor details; the goal is a complete " +
            "record equivalent to watching the video yourself.";
    }
}
