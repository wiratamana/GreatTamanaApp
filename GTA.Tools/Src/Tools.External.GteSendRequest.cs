using System;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace GTA.Tools
{
    internal static class ToolsExternalGte
    {
        [RecordToLog]
        [ToolTag("external", "gte", "network")]
        [ToolInfo(
            name: "gte_send_request",
            description:
                "Send an HTTP request (GET by default, or POST when an optional 'payload' body is " +
                "given) to a locally-running GreatTamanaEngine instance's NetworkServer (loopback-" +
                "only, see NetworkServer.cpp) and return whatever it responds with. Built " +
                "specifically for endpoints like /get_swapchain and /get_game_view, which capture " +
                "the CURRENT rendered frame: if the response is an image (either raw image bytes, or " +
                "a JSON body containing a base64 'data_base64' field — this tool auto-detects and " +
                "decodes either shape) it is returned DIRECTLY as a real, viewable image content " +
                "block (exactly like load_image — never just a path or a wall of base64 text). If " +
                "the response is NOT an image (plain text, an error body, an empty response, or the " +
                "engine isn't reachable at all) a descriptive text string is returned instead, always " +
                "prefixed with the HTTP status code actually received (or a clear connection/timeout " +
                "error if no response was received at all). This tool never throws — every failure " +
                "mode is caught and turned into a readable, verbose error string instead of an " +
                "exception.")]
        [Parameter(index: 0, name: "arguments",
            type: "string",
            description: "The endpoint path (and optional query string) to request, e.g. " +
                         "\"/get_swapchain\", \"/get_game_view\", or \"/get_swapchain?format=json\". " +
                         "A leading '/' is added automatically if missing. Defaults to \"/\" " +
                         "(the server root) if omitted.",
            required: false)]
        [Parameter(index: 1, name: "ip",
            type: "string",
            description: "Host to send the request to, e.g. \"http://127.0.0.1\" or \"127.0.0.1\" " +
                         "(a missing 'http://'/'https://' scheme is added automatically). Defaults to " +
                         "\"http://127.0.0.1\" — GreatTamanaEngine's NetworkServer only ever binds to " +
                         "loopback.",
            required: false)]
        [Parameter(index: 2, name: "port",
            type: "integer",
            description: "TCP port the engine's NetworkServer is listening on. Defaults to 8080.",
            required: false)]
        [Parameter(index: 3, name: "timeout_seconds",
            type: "integer",
            description: "Max seconds to wait for a response before giving up. Defaults to 30.",
            required: false)]
        [Parameter(index: 4, name: "payload",
            type: "string",
            description: "Optional raw request body content to send. When provided (non-empty), the " +
                         "request is sent as an HTTP POST with this exact text as its body instead of " +
                         "a GET — useful for endpoints that take parameters/contents via the body " +
                         "(e.g. a JSON command payload) rather than via the URL/query string. The " +
                         "body's Content-Type is auto-detected: 'application/json; charset=utf-8' if " +
                         "the text looks like JSON (starts with '{' or '[' once trimmed), otherwise " +
                         "'text/plain; charset=utf-8'. Omit (or leave empty) for a plain GET request " +
                         "with no body — the original, default behavior.",
            required: false)]
        public static object GteSendRequest(JsonObject input)
        {
            string url = "(URL not built yet — failed while parsing parameters)";
            try
            {
                // ── Resolve & normalize inputs (never throws — falls back to defaults) ──
                string rawArguments = SafeGetString(input, "arguments") ?? "/";
                string rawIp        = SafeGetString(input, "ip") ?? "http://127.0.0.1";
                int    port         = ToolsIOHelpers.GetOptionalInt(input["port"]) ?? 8080;
                int    timeoutSecs  = ToolsIOHelpers.GetOptionalInt(input["timeout_seconds"]) ?? 30;
                string? payload     = SafeGetString(input, "payload");
                bool    hasPayload  = !string.IsNullOrEmpty(payload);
                string  httpMethod  = hasPayload ? "POST" : "GET";

                if (timeoutSecs <= 0)
                    return $"Error: 'timeout_seconds' must be a positive integer — got {timeoutSecs}. " +
                           "Pass a positive number of seconds (e.g. 30), or omit it to use the default.";
                if (port <= 0 || port > 65535)
                    return $"Error: 'port' must be a valid TCP port number between 1 and 65535 — got " +
                           $"{port}. Pass the actual port GreatTamanaEngine's NetworkServer is bound " +
                           "to, or omit it to use the default (8080).";

                string ip = rawIp.Trim().Trim('"');
                if (ip.Length == 0) ip = "http://127.0.0.1";
                if (!ip.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !ip.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    ip = "http://" + ip;
                }
                ip = ip.TrimEnd('/');

                string path = rawArguments.Trim().Trim('"');
                if (path.Length == 0) path = "/";
                if (!path.StartsWith("/")) path = "/" + path;

                url = $"{ip}:{port}{path}";

                // ── Perform the actual HTTP request (GET, or POST when 'payload' given) ──
                byte[]? bodyBytes;
                int      statusCode;
                string?  contentTypeHeader;

                try
                {
                    using var client = new HttpClient();
                    client.Timeout = TimeSpan.FromSeconds(timeoutSecs);

                    HttpResponseMessage response;
                    if (hasPayload)
                    {
                        string payloadContentType = LooksLikeJson(payload!)
                            ? "application/json"
                            : "text/plain";
                        using var content = new StringContent(payload!, Encoding.UTF8, payloadContentType);
                        response = client.PostAsync(url, content).GetAwaiter().GetResult();
                    }
                    else
                    {
                        response = client
                            .GetAsync(url, HttpCompletionOption.ResponseContentRead)
                            .GetAwaiter().GetResult();
                    }

                    using (response)
                    {
                        statusCode        = (int)response.StatusCode;
                        contentTypeHeader = response.Content.Headers.ContentType?.MediaType;
                        bodyBytes         = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                    }
                }
                catch (OperationCanceledException ex)
                {
                    return $"Error: gte_send_request TIMED OUT after {timeoutSecs}s waiting for a " +
                           $"response from '{url}'. The engine may be busy, unresponsive, or the " +
                           "'timeout_seconds' value is too low for this endpoint (e.g. a slow frame " +
                           $"capture). Raw exception: {ex.GetType().FullName}: {ex.Message}";
                }
                catch (HttpRequestException ex)
                {
                    string statusHint = ex.StatusCode != null ? $", HTTP status {(int)ex.StatusCode}" : "";
                    return $"Error: Could not connect to '{url}' — {ex.Message} " +
                           $"({ex.GetType().FullName}{statusHint}). This usually means " +
                           "GreatTamanaEngine is not currently running, its NetworkServer hasn't " +
                           $"started/bound port {port} yet, or the 'ip'/'port' values passed to " +
                           "gte_send_request don't match the engine's actual bind address (it only " +
                           "ever binds to loopback — see NetworkServer.cpp's kBindHost). Verify the " +
                           "engine process is running and listening on this exact ip:port before " +
                           "retrying.";
                }
                catch (UriFormatException ex)
                {
                    return $"Error: Built an invalid URL '{url}' from ip='{ip}', port={port}, " +
                           $"arguments='{path}' — {ex.Message} ({ex.GetType().FullName}). Check that " +
                           "'ip' is a plain host (e.g. 'http://127.0.0.1' or '127.0.0.1') without a " +
                           "path or query string baked into it — put the path/query in 'arguments' " +
                           "instead.";
                }
                catch (Exception ex)
                {
                    return $"Error: Unexpected exception while sending the HTTP request to '{url}': " +
                           $"{ex.GetType().FullName}: {ex.Message}\nStack trace:\n{ex.StackTrace}";
                }

                // ── Interpret the response body ──────────────────────────────────────
                return InterpretResponse(httpMethod, url, statusCode, contentTypeHeader, bodyBytes);
            }
            catch (Exception ex)
            {
                // Final safety net — this method must NEVER throw, no matter what went wrong
                // above (bad/unexpected input shapes, reflection quirks, etc.).
                return $"Error: gte_send_request failed unexpectedly (target URL attempted: '{url}'): " +
                       $"{ex.GetType().FullName}: {ex.Message}\nStack trace:\n{ex.StackTrace}";
            }
        }

        // ── Response interpretation ──────────────────────────────────────────────

        private static object InterpretResponse(
            string httpMethod, string url, int statusCode, string? contentTypeHeader, byte[]? bodyBytes)
        {
            try
            {
                string statusPrefix = $"HTTP {statusCode} — {httpMethod} {url}" +
                    (string.IsNullOrEmpty(contentTypeHeader) ? "" : $" (Content-Type: {contentTypeHeader})");

                if (bodyBytes == null || bodyBytes.Length == 0)
                    return $"{statusPrefix}\n(empty response body — nothing to display).";

                // ── Case 1: raw image bytes (PNG/JPEG/GIF/WEBP magic sniff) ──────────
                string? magic = ImageUtils.DetectImageMagic(bodyBytes);
                if (magic != null)
                    return BuildImageContent(statusPrefix, magic, bodyBytes);

                // ── Case 2: JSON-wrapped base64 (engine's JsonBase64 shape) ──────────
                string bodyText;
                try
                {
                    bodyText = Encoding.UTF8.GetString(bodyBytes);
                }
                catch (Exception ex)
                {
                    return $"{statusPrefix}\nError: response body ({bodyBytes.Length} byte(s)) is not " +
                           "valid UTF-8 text and does not match any known image signature either — " +
                           $"cannot interpret it. ({ex.GetType().FullName}: {ex.Message})";
                }

                string trimmed = bodyText.TrimStart();
                if (trimmed.StartsWith("{") || trimmed.StartsWith("["))
                {
                    JsonNode? parsed;
                    try
                    {
                        parsed = JsonNode.Parse(bodyText);
                    }
                    catch (Exception ex)
                    {
                        return $"{statusPrefix}\nResponse looked like JSON but failed to parse: " +
                               $"{ex.GetType().FullName}: {ex.Message}\nRaw body (truncated): " +
                               TruncateForError(bodyText);
                    }

                    string? dataBase64 = null;
                    try
                    {
                        dataBase64 = parsed?["data_base64"]?.GetValue<string>();
                    }
                    catch (Exception ex)
                    {
                        return $"{statusPrefix}\nJSON response has a 'data_base64' field but it is not " +
                               $"a string value: {ex.GetType().FullName}: {ex.Message}\nRaw body " +
                               $"(truncated): {TruncateForError(bodyText)}";
                    }

                    if (!string.IsNullOrEmpty(dataBase64))
                    {
                        byte[] decoded;
                        try
                        {
                            string clean = dataBase64.Replace("\n", "").Replace("\r", "").Replace(" ", "");
                            decoded = Convert.FromBase64String(clean);
                        }
                        catch (Exception ex)
                        {
                            return $"{statusPrefix}\nJSON response contained a 'data_base64' field " +
                                   $"({dataBase64.Length} chars), but it failed to decode as valid " +
                                   $"base64: {ex.GetType().FullName}: {ex.Message}";
                        }

                        string? decodedMagic = ImageUtils.DetectImageMagic(decoded);
                        if (decodedMagic != null)
                        {
                            string? widthText  = SafeToString(parsed?["width"]);
                            string? heightText = SafeToString(parsed?["height"]);
                            string  dims       = (widthText != null && heightText != null)
                                ? $" ({widthText}x{heightText})" : "";
                            return BuildImageContent(statusPrefix + dims, decodedMagic, decoded);
                        }

                        return $"{statusPrefix}\nJSON response's 'data_base64' field decoded to " +
                               $"{decoded.Length} byte(s), but they don't match any known image " +
                               "signature (PNG/JPEG/GIF/WEBP) — cannot display it as an image.";
                    }

                    // Valid JSON, but no usable 'data_base64' field — return the raw JSON as text
                    // so the caller can still see whatever the engine actually sent back.
                    return $"{statusPrefix}\n{TruncateForError(bodyText)}";
                }

                // ── Case 3: plain text (error message, unexpected format, etc.) ──────
                return $"{statusPrefix}\n{TruncateForError(bodyText)}";
            }
            catch (Exception ex)
            {
                return $"Error: gte_send_request failed while interpreting the response from '{url}' " +
                       $"(HTTP {statusCode}): {ex.GetType().FullName}: {ex.Message}\nStack trace:\n" +
                       ex.StackTrace;
            }
        }

        private static object BuildImageContent(string statusPrefix, string mediaType, byte[] bytes)
        {
            try
            {
                string b64 = Convert.ToBase64String(bytes);

                return new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = $"{statusPrefix} — {bytes.Length} byte(s), {mediaType}"
                    },
                    new JsonObject
                    {
                        ["type"] = "image",
                        ["source"] = new JsonObject
                        {
                            ["type"]       = "base64",
                            ["media_type"] = mediaType,
                            ["data"]       = b64
                        }
                    }
                };
            }
            catch (Exception ex)
            {
                return $"{statusPrefix}\nError: failed to base64-encode the {bytes.Length}-byte image " +
                       $"response for display: {ex.GetType().FullName}: {ex.Message}";
            }
        }

        // ── Small local helpers (all deliberately exception-swallowing) ──────────

        private static bool LooksLikeJson(string text)
        {
            try
            {
                string trimmed = text.TrimStart();
                return trimmed.Length > 0 && (trimmed[0] == '{' || trimmed[0] == '[');
            }
            catch
            {
                return false;
            }
        }

        private static string TruncateForError(string text)
        {
            try
            {
                const int maxChars = 4000;
                if (text.Length <= maxChars) return text;
                return text[..maxChars] + $"\n… (+{text.Length - maxChars} more chars, truncated)";
            }
            catch
            {
                return "(error truncating response text for display)";
            }
        }

        private static string? SafeToString(JsonNode? node)
        {
            try { return node?.ToString(); }
            catch { return null; }
        }

        private static string? SafeGetString(JsonObject input, string key)
        {
            try
            {
                JsonNode? node = input[key];
                if (node is null) return null;
                if (node is JsonValue jv && jv.TryGetValue(out string? s)) return s;
                return node.ToString();
            }
            catch
            {
                return null;
            }
        }
    }
}
