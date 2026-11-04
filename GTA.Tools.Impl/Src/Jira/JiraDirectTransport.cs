
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GTA.Tools.Impl.Common;

namespace GTA.Tools.Impl.Jira
{
    public static class JiraDirectTransport
    {
        private readonly struct ParsedRequest
        {
            public string Command { get; init; }
            public string? Url { get; init; }
            public string? HttpMethod { get; init; }
            public string? Body { get; init; }
            public Dictionary<string, string>? Headers { get; init; }
            public int TimeoutMs { get; init; }
        }

        private static ParsedRequest Parse(object requestObj)
        {
            string json = JsonSerializer.Serialize(requestObj, ToolsCommonPipe.JsonOptions);
            using var doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            string command = root.TryGetProperty("command", out var c) ? (c.GetString() ?? "") : "";
            string? url = root.TryGetProperty("url", out var u) ? u.GetString() : null;
            string? method = root.TryGetProperty("httpMethod", out var m) ? m.GetString() : null;
            string? body = root.TryGetProperty("body", out var b) ? b.GetString() : null;
            int timeoutMs = (root.TryGetProperty("timeoutMs", out var t) && t.ValueKind == JsonValueKind.Number)
                ? t.GetInt32() : 30_000;

            Dictionary<string, string>? headers = null;
            if (root.TryGetProperty("headers", out var h) && h.ValueKind == JsonValueKind.Object)
            {
                headers = new Dictionary<string, string>();
                foreach (JsonProperty prop in h.EnumerateObject())
                    headers[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                        ? (prop.Value.GetString() ?? "")
                        : prop.Value.ToString();
            }

            return new ParsedRequest
            {
                Command = command,
                Url = url,
                HttpMethod = method,
                Body = body,
                Headers = headers,
                TimeoutMs = timeoutMs,
            };
        }

        // "RestApiCall" — mirrors ToolsCommonPipe.SendCommand
        public static string SendCommand(object requestObj)
        {
            try
            {
                ParsedRequest req = Parse(requestObj);

                if (!string.Equals(req.Command, "RestApiCall", StringComparison.OrdinalIgnoreCase))
                    return $"Error: Unsupported command '{req.Command}' for direct JIRA HTTP transport.";
                if (string.IsNullOrWhiteSpace(req.Url))
                    return "Error: Missing 'url'.";

                var (status, responseBody, transportError) = JiraDirectHttpClient.ExecuteRestApiCall(
                    req.Url!, req.HttpMethod ?? "GET", req.Body, req.Headers, req.TimeoutMs);

                if (transportError != null)
                    return $"Error: {transportError}";

                bool success = status is >= 200 and < 300;
                var sb = new StringBuilder();

                if (!success)
                {
                    sb.AppendLine($"Error: HTTP {status} — REST API call returned a non-2xx status.");
                    if (!string.IsNullOrEmpty(responseBody))
                    {
                        sb.AppendLine("--- API Response Body ---");
                        sb.AppendLine(responseBody);
                    }
                    return sb.ToString().TrimEnd();
                }

                sb.AppendLine("Success: true");
                sb.AppendLine($"HTTP Status: {status}");
                if (!string.IsNullOrEmpty(responseBody))
                {
                    sb.AppendLine("--- API Response Body ---");
                    sb.AppendLine(responseBody);
                }
                return sb.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        // "DownloadImageAsBase64" -> Anthropic image content block
        // mirrors ToolsCommonPipe.SendCommandForImage
        public static object SendCommandForImage(object requestObj)
        {
            try
            {
                ParsedRequest req = Parse(requestObj);
                if (string.IsNullOrWhiteSpace(req.Url))
                    return "Error: Missing 'url'.";

                var (bytes, contentType, transportError) = JiraDirectHttpClient.DownloadBytes(req.Url!, req.TimeoutMs);
                if (transportError != null) return $"Error: {transportError}";
                if (bytes == null || bytes.Length == 0) return "Error: Server returned no image data.";

                string mediaType = ResolveImageMediaType(contentType, req.Url!);
                string base64 = Convert.ToBase64String(bytes);

                return new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "image",
                        ["source"] = new JsonObject
                        {
                            ["type"]       = "base64",
                            ["media_type"] = mediaType,
                            ["data"]       = base64,
                        }
                    }
                };
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        // "DownloadImageAsBase64" -> raw base64 string
        // mirrors ToolsCommonPipe.SendCommandForFileBase64
        public static object SendCommandForFileBase64(object requestObj)
        {
            try
            {
                ParsedRequest req = Parse(requestObj);
                if (string.IsNullOrWhiteSpace(req.Url))
                    return "Error: Missing 'url'.";

                var (bytes, _, transportError) = JiraDirectHttpClient.DownloadBytes(req.Url!, req.TimeoutMs);
                if (transportError != null) return $"Error: {transportError}";
                if (bytes == null || bytes.Length == 0) return "Error: Server returned no file data.";

                return Convert.ToBase64String(bytes);
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        // Same as above, with up to MaxDownloadRetries retries on transient
        //    network errors — mirrors ToolsCommonPipe.SendCommandForFileBase64WithRetry
        public static object SendCommandForFileBase64WithRetry(object requestObj)
        {
            object result = "Error: Download not attempted.";
            string? lastError = null;

            for (int attempt = 1; attempt <= ToolsCommonPipe.MaxDownloadRetries; attempt++)
            {
                result = SendCommandForFileBase64(requestObj);

                if (result is string s)
                {
                    if (!s.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
                        return result; // success

                    if (ToolsCommonPipe.IsNetErr(s) && attempt < ToolsCommonPipe.MaxDownloadRetries)
                    {
                        lastError = s;
                        Thread.Sleep(ToolsCommonPipe.RetryBaseDelayMs * attempt);
                        continue;
                    }
                }
                else
                {
                    return result; // unexpected non-string result — return as-is
                }

                break;
            }

            if (lastError != null && result is string finalErr)
                return finalErr + $" (retried {ToolsCommonPipe.MaxDownloadRetries}×)";

            return result;
        }

        private static string ResolveImageMediaType(string? contentTypeHeader, string url)
        {
            if (!string.IsNullOrWhiteSpace(contentTypeHeader) &&
                contentTypeHeader.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                return contentTypeHeader;

            string lower = url.ToLowerInvariant();
            if (lower.Contains(".jpg") || lower.Contains(".jpeg")) return "image/jpeg";
            if (lower.Contains(".gif"))  return "image/gif";
            if (lower.Contains(".webp")) return "image/webp";
            return "image/png";
        }
    }
}
