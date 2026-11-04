using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace GTA.Tools
{
    internal static class GeminiVideoHelper
    {
        internal static string DefaultApiKey   => GTA.Core.AppConfig.GEMINI_API_KEY;

        /// <summary>Gemini Pro model — highest-quality reasoning, used for jira_read_video.</summary>
        internal const string ProModel = "gemini-3.1-pro-preview";

        internal const string FlashModel = "gemini-3.5-flash";

        // ── Endpoints ─────────────────────────────────────────────────────────
        private const string UploadInitUrl = "https://generativelanguage.googleapis.com/upload/v1beta/files";
        private const string FilesBase     = "https://generativelanguage.googleapis.com/v1beta/";
        private const string InteractUrl   = "https://generativelanguage.googleapis.com/v1beta/interactions";

        private const int MaxRetries = 10;

        internal static async Task<(string FileUri, string FileName)> UploadAsync(
            string          filePath,
            string          mimeType,
            string          apiKey,
            Action<string>? log = null)
        {
            using HttpClient uploadHttp = BuildClient(TimeSpan.FromMinutes(10));

            long fileSize = new FileInfo(filePath).Length;
            log?.Invoke($"    File : {Path.GetFileName(filePath)}  ({fileSize / 1_048_576.0:F1} MB)");

            // ── Step 1 — initiate resumable upload (obtain upload URL) ────────
            log?.Invoke("    Step A : initiating resumable upload…");
            string uploadUrl = await RetryAsync(async () =>
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, UploadInitUrl);
                req.Headers.TryAddWithoutValidation("x-goog-api-key",                      apiKey);
                req.Headers.TryAddWithoutValidation("X-Goog-Upload-Protocol",              "resumable");
                req.Headers.TryAddWithoutValidation("X-Goog-Upload-Command",               "start");
                req.Headers.TryAddWithoutValidation("X-Goog-Upload-Header-Content-Length", fileSize.ToString());
                req.Headers.TryAddWithoutValidation("X-Goog-Upload-Header-Content-Type",   mimeType);

                string displayName = Path.GetFileNameWithoutExtension(filePath);
                req.Content = new StringContent(
                    "{\"file\":{\"display_name\":\"" + displayName.Replace("\"", "'") + "\"}}",
                    Encoding.UTF8, "application/json");

                using var resp = await uploadHttp.SendAsync(req);
                if (!resp.IsSuccessStatusCode)
                {
                    string rb = await resp.Content.ReadAsStringAsync();
                    throw new HttpRequestException(
                        $"HTTP {(int)resp.StatusCode} on upload-init: {rb[..Math.Min(200, rb.Length)]}");
                }
                return resp.Headers.GetValues("x-goog-upload-url").First();
            }, "upload-init", log);

            log?.Invoke("    Step B : uploading file bytes…");

            // ── Step 2 — stream the file bytes to the obtained upload URL ─────
            string uploadJson = await RetryAsync(async () =>
            {
                await using var fs = File.OpenRead(filePath);

                using var req = new HttpRequestMessage(HttpMethod.Post, uploadUrl);
                req.Headers.TryAddWithoutValidation("X-Goog-Upload-Offset",  "0");
                req.Headers.TryAddWithoutValidation("X-Goog-Upload-Command", "upload, finalize");
                req.Content = new StreamContent(fs);
                req.Content.Headers.ContentType   = new MediaTypeHeaderValue(mimeType);
                req.Content.Headers.ContentLength = fileSize;

                using var resp = await uploadHttp.SendAsync(req);
                if (!resp.IsSuccessStatusCode)
                {
                    string rb = await resp.Content.ReadAsStringAsync();
                    throw new HttpRequestException(
                        $"HTTP {(int)resp.StatusCode} on upload-data: {rb[..Math.Min(200, rb.Length)]}");
                }
                return await resp.Content.ReadAsStringAsync();
            }, "upload-data", log);

            var    node     = JsonNode.Parse(uploadJson)!;
            string fileUri  = node["file"]!["uri"]!.GetValue<string>();
            string fileName = node["file"]!["name"]!.GetValue<string>();
            log?.Invoke($"    Upload complete. fileName = {fileName}");
            return (fileUri, fileName);
        }

        internal static async Task WaitForActiveAsync(
            string          fileName,
            string          apiKey,
            int             maxWaitMs,
            Action<string>? log = null)
        {
            using HttpClient apiHttp = BuildClient(TimeSpan.FromSeconds(60));
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(5_000, maxWaitMs));

            for (int poll = 1; ; poll++)
            {
                string json = await RetryAsync(async () =>
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, $"{FilesBase}{fileName}");
                    req.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);

                    using var resp = await apiHttp.SendAsync(req);
                    resp.EnsureSuccessStatusCode();
                    return await resp.Content.ReadAsStringAsync();
                }, $"poll-{poll}", log);

                string state = JsonNode.Parse(json)!["state"]!.GetValue<string>();
                log?.Invoke($"    Poll #{poll} : state = {state}");

                if (state == "ACTIVE") return;
                if (state == "FAILED") throw new Exception("Gemini File API reported FAILED state for the uploaded video.");

                if (DateTime.UtcNow >= deadline)
                    throw new TimeoutException(
                        $"Gemini File API did not reach ACTIVE state within {maxWaitMs / 1000} second(s).");

                await Task.Delay(5_000);
            }
        }

        internal static async Task<string?> AskAsync(
            string          fileUri,
            string          mimeType,
            string          prompt,
            string          model,
            string          apiKey,
            Action<string>? log = null)
        {
            using HttpClient apiHttp = BuildClient(TimeSpan.FromMinutes(3));

            // Per Gemini docs, place the text part AFTER the video part in the input array.
            string body = System.Text.Json.JsonSerializer.Serialize(new
            {
                model,
                input = new object[]
                {
                    new { type = "video", uri = fileUri, mime_type = mimeType },
                    new { type = "text",  text = prompt }
                }
            });

            string respJson = await RetryAsync(async () =>
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, InteractUrl);
                req.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");

                using var resp = await apiHttp.SendAsync(req);
                string rb = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                    throw new HttpRequestException(
                        $"HTTP {(int)resp.StatusCode} on interactions: {rb[..Math.Min(400, rb.Length)]}");
                return rb;
            }, "interactions", log);

            var node = JsonNode.Parse(respJson);

            // Try output_text shortcut first, then steps[*].content[0].text
            string? text = node?["output_text"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                var steps = node?["steps"]?.AsArray();
                if (steps is not null)
                    foreach (var step in steps)
                    {
                        text = step?["content"]?[0]?["text"]?.GetValue<string>()?.Trim();
                        if (!string.IsNullOrWhiteSpace(text)) break;
                    }
            }

            return text;
        }

        internal static async Task<string?> AskTextAsync(
            string          prompt,
            string          model,
            string          apiKey,
            Action<string>? log = null)
        {
            using HttpClient apiHttp = BuildClient(TimeSpan.FromMinutes(3));

            string body = System.Text.Json.JsonSerializer.Serialize(new
            {
                model,
                input = new object[]
                {
                    new { type = "text", text = prompt }
                }
            });

            string respJson = await RetryAsync(async () =>
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, InteractUrl);
                req.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");

                using var resp = await apiHttp.SendAsync(req);
                string rb = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                    throw new HttpRequestException(
                        $"HTTP {(int)resp.StatusCode} on interactions (text): {rb[..Math.Min(400, rb.Length)]}");
                return rb;
            }, "interactions-text", log);

            var node = JsonNode.Parse(respJson);

            // Try output_text shortcut first, then steps[*].content[0].text
            string? text = node?["output_text"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                var steps = node?["steps"]?.AsArray();
                if (steps is not null)
                    foreach (var step in steps)
                    {
                        text = step?["content"]?[0]?["text"]?.GetValue<string>()?.Trim();
                        if (!string.IsNullOrWhiteSpace(text)) break;
                    }
            }

            return text;
        }

        private static async Task<T> RetryAsync<T>(
            Func<Task<T>>   action,
            string          opName,
            Action<string>? log)
        {
            for (int attempt = 1; attempt <= MaxRetries; attempt++)
            {
                try
                {
                    return await action();
                }
                catch (Exception ex) when (IsConnError(ex) && attempt < MaxRetries)
                {
                    log?.Invoke($"    [{opName}] conn error (attempt {attempt}/{MaxRetries}) — retrying in {attempt}s…");
                    await Task.Delay(1_000 * attempt); // linear back-off
                }
            }
            throw new InvalidOperationException("unreachable");
        }

        private static bool IsConnError(Exception ex)
        {
            string msg = (ex.InnerException?.Message ?? ex.Message).ToLowerInvariant();
            return msg.Contains("forcibly")  ||
                   msg.Contains("transport") ||
                   msg.Contains("reset")     ||
                   msg.Contains("aborted")   ||
                   (msg.Contains("connection") && !msg.Contains("http"));
        }

        // Proxy: Windows system proxy + current Windows login (SSO). Direct when Windows uses no proxy.
        private static HttpClient BuildClient(TimeSpan timeout)
        {
            var handler = new HttpClientHandler
            {
                Proxy    = GTA.Core.SystemProxy.CreateWebProxy(),
                UseProxy = true,
                DefaultProxyCredentials = CredentialCache.DefaultCredentials,
            };
            return new HttpClient(handler) { Timeout = timeout };
        }
    }
}
