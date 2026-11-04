using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GTA.Tools
{
    internal static class ToolsExternalHttp
    {
        private const string DefaultBase           = "http://127.0.0.1:8080";
        private const int    DefaultTimeoutSeconds = 30;
        private const int    MaxTimeoutSeconds     = 600;
        private const int    DefaultMaxChars       = 20_000;
        private const int    MinMaxChars           = 500;
        private const int    MaxMaxChars           = 200_000;
        private const long   MaxResponseBytes      = 64L * 1024 * 1024;

        // false = only loopback hosts are allowed. See the header comment.
        private static readonly bool AllowNonLoopback = false;

        private static readonly HashSet<string> AllowedMethods =
            new(StringComparer.OrdinalIgnoreCase) { "GET", "POST", "PUT", "DELETE", "PATCH", "HEAD" };

        // One path segment: ["quoted key"], ['quoted key'], [3], or a plain name / number.
        private static readonly Regex SegRegex = new(
            @"\[\s*""(?<q>[^""]*)""\s*\]|\[\s*'(?<q2>[^']*)'\s*\]|\[\s*(?<i>\d+)\s*\]|(?<p>[^.\[\]]+)",
            RegexOptions.CultureInvariant);

        private readonly record struct Seg(string Text, bool IsIndex, bool Quoted);

        // Keeps non-ASCII text readable in json_select results.
        private static readonly JsonSerializerOptions RelaxedJson = new()
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        [RecordToLog]
        [ToolTag("external", "http", "network")]
        [ToolInfo(
            name: "http_request",
            description:
                "Send an HTTP request to a LOCAL server (loopback only: 127.0.0.1 / localhost / ::1) — " +
                "mainly the GreatTamanaEngine editor's HTTP server on port 8080 (e.g. /profiler, " +
                "/render_graph, /feature_debug/state?name=GpuDrivenScene, /get_logs, POST /instantiate_asset). " +
                "It runs INSIDE this app: no powershell.exe / curl.exe / python.exe is started, so " +
                "endpoint security cannot flag it. USE THIS INSTEAD OF run_shell with Invoke-RestMethod, " +
                "Invoke-WebRequest or curl for local calls (those PowerShell cmdlets are blocked in run_shell). " +
                "'url' may be just a path like \"/profiler\" (host defaults to http://127.0.0.1:8080). " +
                "Method defaults to GET, or POST when 'body' is given. Returns the HTTP status line " +
                "(status, method, URL, content type, size, milliseconds) followed by the response text. " +
                "BIG RESPONSES: text longer than 'max_chars' (default 20000) is cut to a head+tail preview " +
                "and the FULL text is saved to a temp file whose path is returned. To avoid dumping a big " +
                "JSON, use 'json_select' (e.g. \"fields.game.occlusion\" or \"render_features[0].name\") to " +
                "return just one part, or 'save_to_file' to store the whole body on disk (then parse it " +
                "with inline_python). Redirects are NOT followed. Images are NOT displayed — use " +
                "gte_send_request for /get_game_view or /get_swapchain. Non-2xx statuses are returned as " +
                "normal text (look at the status line); connection failures and timeouts return a clear " +
                "'Error: ...' string. This tool never throws.")]
        [Parameter(index: 0, name: "url",
            type: "string",
            description: "Full URL (e.g. \"http://127.0.0.1:8080/profiler\") or just a path " +
                         "(e.g. \"/render_graph?x=1\", host defaults to http://127.0.0.1:8080). " +
                         "Only loopback hosts are allowed.",
            required: true)]
        [Parameter(index: 1, name: "method",
            type: "string",
            description: "Optional. GET, POST, PUT, DELETE, PATCH or HEAD. Defaults to GET, or POST " +
                         "when 'body' is given.",
            required: false)]
        [Parameter(index: 2, name: "body",
            type: "string",
            description: "Optional request body text (e.g. a JSON command). Content-Type is " +
                         "application/json if the body starts with '{' or '[', otherwise text/plain, " +
                         "unless 'content_type' says otherwise. Not allowed with GET/HEAD.",
            required: false)]
        [Parameter(index: 3, name: "headers",
            type: "string",
            description: "Optional extra request headers as a JSON object string, e.g. " +
                         "{\"Accept\":\"application/json\"}.",
            required: false)]
        [Parameter(index: 4, name: "content_type",
            type: "string",
            description: "Optional Content-Type for the body, e.g. \"application/json; charset=utf-8\". " +
                         "Overrides the automatic choice.",
            required: false)]
        [Parameter(index: 5, name: "timeout_seconds",
            type: "integer",
            description: "Optional. Max seconds to wait for the response. Default 30, maximum 600.",
            required: false)]
        [Parameter(index: 6, name: "json_select",
            type: "string",
            description: "Optional. Return only one part of a JSON response. Dotted path with array " +
                         "indexes, e.g. \"render_features[0].name\" or \"fields.game.occlusion\". " +
                         "Keys that contain dots (like \"game.occlusion\") work both as " +
                         "fields.game.occlusion and as fields[\"game.occlusion\"]. If the path is wrong, " +
                         "the error lists the keys that exist at that level.",
            required: false)]
        [Parameter(index: 7, name: "save_to_file",
            type: "string",
            description: "Optional absolute path. The raw response body (any content, also binary) is " +
                         "saved there (an existing file is overwritten) and only a short preview is returned.",
            required: false)]
        [Parameter(index: 8, name: "max_chars",
            type: "integer",
            description: "Optional. Max characters of response text to return (500–200000, default " +
                         "20000). Longer text is cut to head+tail and the full text is saved to a temp file.",
            required: false)]
        public static object HttpRequest(JsonObject input)
        {
            string shownUrl = "(URL not built yet)";
            try
            {
                // ── url ─────────────────────────────────────────────────────────────
                string rawUrl = (GetString(input, "url") ?? "").Trim().Trim('"');
                if (rawUrl.Length == 0)
                    return "Error: 'url' is required, e.g. \"/profiler\" or \"http://127.0.0.1:8080/render_graph\".";

                if (rawUrl.StartsWith("/", StringComparison.Ordinal)) rawUrl = DefaultBase + rawUrl;
                else if (!rawUrl.Contains("://", StringComparison.Ordinal)) rawUrl = "http://" + rawUrl;
                shownUrl = rawUrl;

                if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out Uri? uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                    return $"Error: '{rawUrl}' is not a valid http:// or https:// URL.";

                if (!AllowNonLoopback && !IsLoopbackHost(uri.Host))
                    return $"Error: http_request only talks to the local machine (127.0.0.1 / localhost / ::1), " +
                           $"but the host is '{uri.Host}'. For other hosts use a dedicated tool (jira_*, box_*, git_*) " +
                           "or a Python script.";

                // ── method / body ───────────────────────────────────────────────────
                string? body    = GetString(input, "body");
                bool    hasBody = !string.IsNullOrEmpty(body);
                string  method  = (GetString(input, "method") ?? "").Trim().ToUpperInvariant();
                if (method.Length == 0) method = hasBody ? "POST" : "GET";

                if (!AllowedMethods.Contains(method))
                    return $"Error: method '{method}' is not supported. Use GET, POST, PUT, DELETE, PATCH or HEAD.";
                if (hasBody && (method == "GET" || method == "HEAD"))
                    return $"Error: a {method} request cannot have a 'body'. Set method to POST (or PUT/PATCH), " +
                           "or leave 'method' out.";

                // ── numbers ─────────────────────────────────────────────────────────
                int timeoutSeconds = GetInt(input, "timeout_seconds") ?? DefaultTimeoutSeconds;
                if (timeoutSeconds <= 0)
                    return $"Error: 'timeout_seconds' must be a positive number — got {timeoutSeconds}.";
                if (timeoutSeconds > MaxTimeoutSeconds) timeoutSeconds = MaxTimeoutSeconds;

                int maxChars = GetInt(input, "max_chars") ?? DefaultMaxChars;
                maxChars = Math.Clamp(maxChars, MinMaxChars, MaxMaxChars);

                // ── headers ─────────────────────────────────────────────────────────
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                string? headersText = GetString(input, "headers");
                if (!string.IsNullOrWhiteSpace(headersText))
                {
                    JsonNode? parsedHeaders;
                    try { parsedHeaders = JsonNode.Parse(headersText); }
                    catch (JsonException ex)
                    {
                        return "Error: 'headers' must be a JSON object string like {\"Accept\":\"application/json\"}. " +
                               $"Parse error: {ex.Message}";
                    }
                    if (parsedHeaders is not JsonObject headerObject)
                        return "Error: 'headers' must be a JSON object string like {\"Accept\":\"application/json\"}.";
                    foreach (var kv in headerObject)
                        headers[kv.Key] = kv.Value?.ToString() ?? "";
                }

                headers.Remove("Content-Type", out string? contentTypeFromHeaders);
                string? contentType = GetString(input, "content_type");
                if (string.IsNullOrWhiteSpace(contentType)) contentType = contentTypeFromHeaders;
                if (string.IsNullOrWhiteSpace(contentType) && hasBody)
                    contentType = LooksLikeJson(body!) ? "application/json; charset=utf-8" : "text/plain; charset=utf-8";

                // ── request ─────────────────────────────────────────────────────────
                using var handler = new SocketsHttpHandler
                {
                    UseProxy               = false,   // loopback never needs the corporate proxy
                    AllowAutoRedirect      = false,   // a redirect could leave loopback
                    AutomaticDecompression = DecompressionMethods.All,
                };
                using var client = new HttpClient(handler, disposeHandler: false)
                {
                    Timeout                      = TimeSpan.FromSeconds(timeoutSeconds),
                    MaxResponseContentBufferSize = MaxResponseBytes,
                };
                using var request = new HttpRequestMessage(new HttpMethod(method), uri);

                StringContent? content = null;
                if (hasBody)
                {
                    content = new StringContent(body!, new UTF8Encoding(false));
                    content.Headers.Remove("Content-Type");
                    content.Headers.TryAddWithoutValidation("Content-Type", contentType);
                    request.Content = content;
                }

                foreach (var kv in headers)
                {
                    bool added = request.Headers.TryAddWithoutValidation(kv.Key, kv.Value) ||
                                 (content != null && content.Headers.TryAddWithoutValidation(kv.Key, kv.Value));
                    if (!added)
                        return $"Error: could not add the header '{kv.Key}'. It is not a valid request header " +
                               "(or it is a body header and there is no 'body').";
                }

                // ── send ────────────────────────────────────────────────────────────
                var sw = Stopwatch.StartNew();
                HttpResponseMessage response;
                try
                {
                    response = client.SendAsync(request, HttpCompletionOption.ResponseContentRead)
                                     .GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    return $"Error: http_request TIMED OUT after {timeoutSeconds}s waiting for '{shownUrl}'. " +
                           "The editor may be busy or frozen. Retry with a larger 'timeout_seconds' if the " +
                           "endpoint is slow (e.g. a frame capture).";
                }
                catch (HttpRequestException ex)
                {
                    string inner = ex.InnerException != null ? $" / {ex.InnerException.Message}" : "";
                    return $"Error: could not get a response from '{shownUrl}' — {ex.Message}{inner}. " +
                           "Usually the editor is not running, its HTTP server has not started yet, the port is " +
                           "wrong, or the editor was closed or killed (for example by anti-virus) during the call.";
                }

                using (response)
                {
                    byte[] bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                    sw.Stop();

                    int     status      = (int)response.StatusCode;
                    string  reason      = response.ReasonPhrase ?? "";
                    string? ctResponse  = response.Content.Headers.ContentType?.ToString();
                    string? location    = response.Headers.Location?.ToString();

                    var head = new StringBuilder();
                    head.Append($"HTTP {status} {reason} — {method} {shownUrl} ")
                        .Append($"(Content-Type: {ctResponse ?? "none"}, {bytes.Length:N0} bytes, {sw.ElapsedMilliseconds} ms)");
                    if (!string.IsNullOrEmpty(location))
                        head.Append($"\nLocation: {location}  (redirects are NOT followed)");

                    // ── save_to_file ────────────────────────────────────────────────
                    string? saveTo = GetString(input, "save_to_file");
                    bool    saved  = false;
                    if (!string.IsNullOrWhiteSpace(saveTo))
                    {
                        string path = saveTo.Trim().Trim('"');
                        if (!Path.IsPathRooted(path))
                        {
                            head.Append("\nError: 'save_to_file' must be an absolute path — nothing was saved.");
                        }
                        else
                        {
                            try
                            {
                                string? dir = Path.GetDirectoryName(path);
                                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                                File.WriteAllBytes(path, bytes);
                                saved = true;
                                head.Append($"\nSaved the full body ({bytes.Length:N0} bytes) to: {path}");
                            }
                            catch (Exception ex)
                            {
                                head.Append($"\nError: could not save to '{path}': {ex.Message}");
                            }
                        }
                    }

                    if (bytes.Length == 0)
                        return head + "\n(empty response body)";

                    // ── not text ────────────────────────────────────────────────────
                    string? imageType = ImageUtils.DetectImageMagic(bytes);
                    if (imageType != null)
                        return head + $"\nThe response is an image ({imageType}). http_request does not display " +
                               "images — use gte_send_request to view it" +
                               (saved ? "." : ", or pass save_to_file to keep the file.");

                    if (!TryDecodeUtf8(bytes, out string text))
                        return head + "\nThe response is binary, not text." +
                               (saved ? "" : " Pass save_to_file to keep it.");

                    // ── json_select ─────────────────────────────────────────────────
                    string? jsonSelect = GetString(input, "json_select");
                    if (!string.IsNullOrWhiteSpace(jsonSelect))
                    {
                        JsonNode? root = null;
                        bool isJson = true;
                        try { root = JsonNode.Parse(text); }
                        catch (JsonException) { isJson = false; }

                        if (!isJson)
                        {
                            head.Append("\njson_select was ignored: the response body is not valid JSON.");
                        }
                        else
                        {
                            var (selected, error) = SelectJson(root, jsonSelect.Trim());
                            if (error != null)
                                return head + $"\njson_select \"{jsonSelect.Trim()}\" failed: {error}";

                            head.Append($"\njson_select: {jsonSelect.Trim()}");
                            text = selected ?? "null";
                        }
                    }

                    // ── size limit ──────────────────────────────────────────────────
                    int limit = saved ? Math.Min(maxChars, 2_000) : maxChars;
                    return head + "\n\n" + LimitText(text, limit, saved);
                }
            }
            catch (Exception ex)
            {
                return $"Error: http_request failed unexpectedly (URL: '{shownUrl}'): " +
                       $"{ex.GetType().FullName}: {ex.Message}";
            }
        }

        // ── JSON path selection ─────────────────────────────────────────────────

        private static (string? Result, string? Error) SelectJson(JsonNode? root, string path)
        {
            var segs = new List<Seg>();
            foreach (Match m in SegRegex.Matches(path))
            {
                if (m.Groups["q"].Success)       segs.Add(new Seg(m.Groups["q"].Value,  false, true));
                else if (m.Groups["q2"].Success) segs.Add(new Seg(m.Groups["q2"].Value, false, true));
                else if (m.Groups["i"].Success)  segs.Add(new Seg(m.Groups["i"].Value,  true,  false));
                else                             segs.Add(new Seg(m.Groups["p"].Value.Trim(), false, false));
            }
            if (segs.Count == 0)
                return (null, "the path is empty.");

            JsonNode? cur = root;
            string walked = "$";

            for (int i = 0; i < segs.Count; i++)
            {
                Seg seg = segs[i];

                if (cur is JsonArray arr)
                {
                    if (!int.TryParse(seg.Text, out int idx))
                        return (null, $"at {walked} the value is an array with {arr.Count} item(s), " +
                                      $"but '{seg.Text}' is not an index. Use [0] .. [{Math.Max(0, arr.Count - 1)}].");
                    if (idx < 0 || idx >= arr.Count)
                        return (null, $"at {walked} the array has {arr.Count} item(s); index {idx} is out of range.");
                    cur = arr[idx];
                    walked += $"[{idx}]";
                    continue;
                }

                if (cur is JsonObject obj)
                {
                    string key  = seg.Text;
                    int    used = 1;

                    // A key may itself contain dots ("game.occlusion"): try joining the next plain segments.
                    if (!obj.ContainsKey(key) && !seg.Quoted && !seg.IsIndex)
                    {
                        string joined = key;
                        for (int j = i + 1; j < segs.Count && !segs[j].Quoted && !segs[j].IsIndex; j++)
                        {
                            joined += "." + segs[j].Text;
                            if (obj.ContainsKey(joined))
                            {
                                key  = joined;
                                used = j - i + 1;
                                break;
                            }
                        }
                    }

                    if (!obj.TryGetPropertyValue(key, out JsonNode? next))
                    {
                        string keys = string.Join(", ", obj.Select(kv => kv.Key).Take(40));
                        if (obj.Count > 40) keys += $", … (+{obj.Count - 40} more)";
                        return (null, $"at {walked} there is no key '{seg.Text}'. Keys here: {keys}");
                    }

                    cur = next;
                    walked += key.Contains('.') || key.Contains(' ') ? $"[\"{key}\"]" : $".{key}";
                    i += used - 1;
                    continue;
                }

                return (null, $"at {walked} the value is {(cur == null ? "null" : "a plain value")}; " +
                              $"it has nothing named '{seg.Text}'.");
            }

            return (cur?.ToJsonString(RelaxedJson) ?? "null", null);
        }

        // ── helpers ─────────────────────────────────────────────────────────────

        private static string LimitText(string text, int maxChars, bool alreadySaved)
        {
            if (text.Length <= maxChars) return text;

            int head = maxChars * 2 / 3;
            int tail = maxChars - head;
            string note;
            if (alreadySaved)
            {
                note = "(preview only — the full body was saved to the file named above)";
            }
            else
            {
                try
                {
                    string tmp = Path.Combine(Path.GetTempPath(), $"http_request_full_{Guid.NewGuid():N}.txt");
                    File.WriteAllText(tmp, text, new UTF8Encoding(false));
                    note = $"(preview only — the FULL text was saved to: {tmp})";
                }
                catch (Exception ex)
                {
                    note = $"(preview only — the full text could not be saved: {ex.Message})";
                }
            }

            int omitted = text.Length - head - tail;
            return $"⚠ Response text is {text.Length:N0} characters; showing the start and the end. {note}\n\n" +
                   text.Substring(0, head) +
                   $"\n\n… [{omitted:N0} characters omitted] …\n\n" +
                   text.Substring(text.Length - tail);
        }

        private static bool IsLoopbackHost(string host)
        {
            string h = host.Trim().Trim('[', ']');
            if (h.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
            return IPAddress.TryParse(h, out IPAddress? ip) && IPAddress.IsLoopback(ip);
        }

        private static bool LooksLikeJson(string text)
        {
            string t = text.TrimStart();
            return t.Length > 0 && (t[0] == '{' || t[0] == '[');
        }

        private static bool TryDecodeUtf8(byte[] bytes, out string text)
        {
            try
            {
                text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                    .GetString(bytes);
                if (text.Length > 0 && text[0] == '\uFEFF') text = text.Substring(1);
                return true;
            }
            catch (DecoderFallbackException)
            {
                text = "";
                return false;
            }
        }

        private static string? GetString(JsonObject input, string key)
        {
            try
            {
                JsonNode? node = input[key];
                if (node is null) return null;
                if (node is JsonValue v && v.TryGetValue(out string? s)) return s;
                return node.ToJsonString();
            }
            catch
            {
                return null;
            }
        }

        private static int? GetInt(JsonObject input, string key)
        {
            try
            {
                JsonNode? node = input[key];
                if (node is null) return null;
                if (node is JsonValue v)
                {
                    if (v.TryGetValue(out int i)) return i;
                    if (v.TryGetValue(out long l)) return (int)Math.Clamp(l, int.MinValue, int.MaxValue);
                    if (v.TryGetValue(out double d)) return (int)d;
                    if (v.TryGetValue(out string? s) && int.TryParse(s, out int parsed)) return parsed;
                }
                return null;
            }
            catch
            {
                return null;
            }
        }
    }
}
