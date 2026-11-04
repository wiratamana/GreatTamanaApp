using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace GTA.Core
{

    public sealed class GeminiTranslator : ILlmClient
    {
        // ── Gemini safety cap (generous — matches the other backends' bookkeeping-only caps) ──
        private const int GEMINI_MAX_OUTPUT = 1_000_000;

        // ── API root — streaming streamGenerateContent endpoint ───────────────
        private const string API_ROOT = "https://generativelanguage.googleapis.com/v1beta/models/";

        // ─────────────────────────────────────────────────────────────────────

        /// <inheritdoc/>
        public string ProviderName => "Gemini";

        /// <inheritdoc/>
        /// <remarks>Matches the section name expected in model_pricing.ini.</remarks>
        public string ModelName => AppConfig.GEMINI_MODEL;

        /// <inheritdoc/>
        public async Task<JsonObject?> CallAsync(
            JsonArray         messages,
            JsonArray         tools,
            string            systemPrompt,
            int               maxTokens,
            string            thinking,   // "adaptive" | "disabled" → thinkingConfig.thinkingBudget
            string            effort,     // none/low/medium/high/xhigh/max → thinkingBudget token count
            bool              debug,
            IAgentLogger      logger,
            IStreamRenderer   renderer,
            CancellationToken ct = default)
        {
            logger.LastCallExceededContextLength = false;

            string url = BuildUrl();

            // ── Translate to Gemini format ─────────────────────────────────────
            JsonArray geminiContentsOriginal = TranslateMessages(messages);
            JsonArray geminiTools            = TranslateTools(tools);
            int       safeTokens             = Math.Min(maxTokens, GEMINI_MAX_OUTPUT);
            int       thinkingBudget         = ResolveThinkingBudget(thinking, effort);

            JsonArray activeContents = geminiContentsOriginal;

            string? partialTextToMerge = null;

            // ── Manual "Retry" support (UI) — mirrors GptTranslator/AzureApiClient ──
            CancellationTokenSource? liveAttemptCts      = null;
            bool                     manualRetryRequested = false;
            void CancelLiveAttempt()
            {
                manualRetryRequested = true;
                try { liveAttemptCts?.Cancel(); } catch { /* already cancelled/disposed */ }
            }

            for (int attempt = 1; attempt <= AppConfig.MAX_RETRIES; attempt++)
            {
                if (attempt > 1)
                    await Task.Delay(AppConfig.RETRY_DELAY_MS, ct);

                GeminiStreamResult? streamResult = null;

                try
                {
                    string payloadJson = BuildPayload(
                        activeContents, geminiTools, systemPrompt, safeTokens, thinkingBudget).ToJsonString();

                    HttpClient client = _sharedProxyClient.Value; // shared/reused — do NOT dispose

                    HttpResponseMessage? response = null;

                    logger.OnConnectionWait(true, CancelLiveAttempt);
                    try
                    {
                        for (int connectAttempt = 1; connectAttempt <= AppConfig.MAX_CONNECT_RETRIES; connectAttempt++)
                        {
                            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            attemptCts.CancelAfter(TimeSpan.FromSeconds(AppConfig.HEADERS_TIMEOUT_SECONDS));
                            liveAttemptCts = attemptCts;

                            try
                            {
                                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                                request.Headers.TryAddWithoutValidation("x-goog-api-key", AppConfig.GEMINI_API_KEY);
                                request.Content =
                                    new StringContent(payloadJson, Encoding.UTF8, "application/json");

                                response = await client.SendAsync(
                                    request, HttpCompletionOption.ResponseHeadersRead, attemptCts.Token);
                                break; // headers received (success or non-2xx) — stop retrying connect
                            }
                            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                            {
                                bool wasManualRetry = manualRetryRequested;
                                manualRetryRequested = false;

                                if (connectAttempt >= AppConfig.MAX_CONNECT_RETRIES)
                                {
                                    logger.LogError($"[Gemini] Giving up after {connectAttempt} connect attempts, each exceeding the {AppConfig.HEADERS_TIMEOUT_SECONDS}s headers-wait timeout.");
                                    throw new TimeoutException(
                                        $"[Gemini] SendAsync exceeded the {AppConfig.HEADERS_TIMEOUT_SECONDS}s headers-wait timeout on all {connectAttempt} attempts.");
                                }
                                logger.LogInfo(wasManualRetry
                                    ? $"[Gemini][ManualRetry] User clicked Retry — cancelling and starting a fresh connection (connect attempt {connectAttempt}/{AppConfig.MAX_CONNECT_RETRIES})."
                                    : $"[Gemini] Retry because SendAsync exceeded the {AppConfig.HEADERS_TIMEOUT_SECONDS}s headers-wait timeout (connect attempt {connectAttempt}/{AppConfig.MAX_CONNECT_RETRIES}).");
                                if (!wasManualRetry)
                                    await Task.Delay(AppConfig.CONNECT_RETRY_DELAY_MS, ct);
                            }
                        }
                    }
                    finally
                    {
                        liveAttemptCts = null;
                        logger.OnConnectionWait(false);
                    }

                    if (response is null)
                        throw new InvalidOperationException("[Gemini] SendAsync produced no response and threw no exception — unexpected state.");

                    if (!response.IsSuccessStatusCode)
                    {
                        string raw = await response.Content.ReadAsStringAsync(ct);

                        bool isTransient = response.StatusCode is
                            HttpStatusCode.ServiceUnavailable or   // 503
                            HttpStatusCode.BadGateway          or  // 502
                            HttpStatusCode.GatewayTimeout      or  // 504
                            (HttpStatusCode)429;                   // Too Many Requests

                        if (!isTransient || attempt >= AppConfig.MAX_RETRIES)
                        {
                            string errMsg = TryExtractError(raw);
                            // See LlmCallDiagnostics — lets AgenticLoop tell "prompt too
                            // long" apart from any other unrecoverable error.
                            logger.LastCallExceededContextLength =
                                LlmCallDiagnostics.LooksLikeContextLengthError(errMsg);
                            logger.LogError($"[Gemini] HTTP {(int)response.StatusCode}: {errMsg}");
                            logger.LogError("[Gemini] Non-retriable error. Giving up.");
                            return null;
                        }

                        logger.LogWarning(
                            $"[Gemini] [Retry {attempt}/{AppConfig.MAX_RETRIES}] HTTP {(int)response.StatusCode}");

                        // Fresh HTTP-level failure before any content streamed —
                        // retry the exact original request, no partial to merge.
                        activeContents     = geminiContentsOriginal;
                        partialTextToMerge = null;
                        continue;
                    }

                    using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    streamCts.CancelAfter(TimeSpan.FromSeconds(AppConfig.STREAM_TIMEOUT_SECONDS));
                    using System.IO.Stream stream = await response.Content.ReadAsStreamAsync(streamCts.Token);

                    streamResult = await GeminiStreamParser.ParseAsync(
                        stream, debug, renderer, streamCts.Token,
                        onActivity: () => streamCts.CancelAfter(TimeSpan.FromSeconds(AppConfig.STREAM_TIMEOUT_SECONDS)));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    if (debug || attempt >= AppConfig.MAX_RETRIES)
                    {
                        logger.LogError($"[Gemini] {ex.Message}");
                        if (ex.InnerException != null)
                            logger.LogError($"[Gemini] Inner: {ex.InnerException.Message}");
                    }
                }

                // ── Success ───────────────────────────────────────────────────────
                if (streamResult != null && !streamResult.WasInterrupted)
                {
                    JsonArray finalBlocks = streamResult.ContentBlocks;
                    if (partialTextToMerge != null)
                        MergePartialText(finalBlocks, partialTextToMerge);

                    int freshInputTokens = Math.Max(0, streamResult.InputTokens - streamResult.CachedTokens);

                    if (debug)
                        logger.LogInfo(
                            $"[Gemini] in={freshInputTokens} " +
                            $"out={streamResult.CandidatesTokens + streamResult.ThoughtsTokens} " +
                            $"cached={streamResult.CachedTokens} " +
                            $"thinkingBudget={thinkingBudget}");

                    return new JsonObject
                    {
                        ["stop_reason"] = streamResult.StopReason,
                        ["content"]     = finalBlocks,
                        ["usage"] = new JsonObject
                        {
                            ["input_tokens"]                = freshInputTokens,
                            ["output_tokens"]               = streamResult.CandidatesTokens + streamResult.ThoughtsTokens,
                            ["cache_creation_input_tokens"] = 0, // N/A for Gemini via this endpoint
                            ["cache_read_input_tokens"]     = streamResult.CachedTokens,
                        },
                    };
                }

                // ── Error / interrupted — decide retry strategy ────────────────────
                if (attempt >= AppConfig.MAX_RETRIES)
                {
                    logger.LogError($"[Gemini] Max retries ({AppConfig.MAX_RETRIES}) reached. Giving up.");
                    return null;
                }

                bool hasPartial = streamResult != null && streamResult.ContentBlocks.Count > 0;

                if (hasPartial)
                {
                    logger.LogWarning(
                        $"[Gemini] [Retry {attempt}/{AppConfig.MAX_RETRIES}] stream cut, continuing");

                    JsonArray partialBlocks = streamResult!.ContentBlocks;
                    partialTextToMerge = ExtractPartialText(partialBlocks);

                    JsonArray modelParts = ContentBlocksToGeminiParts(partialBlocks);
                    var contModelMsg = new JsonObject { ["role"] = "model", ["parts"] = modelParts };

                    var pendingToolNames = partialBlocks
                        .Where(b => b?["type"]?.GetValue<string>() == "tool_use")
                        .Select(b => b!["name"]?.GetValue<string>() ?? "")
                        .Where(n => !string.IsNullOrEmpty(n))
                        .ToList();

                    var pendingToolIds = partialBlocks
                        .Where(b => b?["type"]?.GetValue<string>() == "tool_use")
                        .Select(b => b!["id"]?.GetValue<string>() ?? "")
                        .Where(id => !string.IsNullOrEmpty(id))
                        .ToList();
                    foreach (string abandonedId in pendingToolIds)
                        logger.LogToolAbandoned(abandonedId,
                            "connection interrupted mid-call before this tool actually ran; " +
                            "retried automatically.");

                    var contUserParts = new JsonArray();
                    foreach (string name in pendingToolNames)
                    {
                        contUserParts.Add(new JsonObject
                        {
                            ["functionResponse"] = new JsonObject
                            {
                                ["name"]     = name,
                                ["response"] = new JsonObject
                                {
                                    ["result"] = "This tool call was interrupted by a network error " +
                                                 "before it could run and was NOT executed. Please " +
                                                 "retry it (or continue) now."
                                },
                            },
                        });
                    }
                    contUserParts.Add(new JsonObject
                    {
                        ["text"] = "Your response was interrupted by a network error. " +
                                   "Please continue exactly from where you left off, " +
                                   "without repeating anything you already said."
                    });

                    JsonArray contContents = geminiContentsOriginal.DeepClone().AsArray();
                    contContents.Add(contModelMsg);
                    contContents.Add(new JsonObject { ["role"] = "user", ["parts"] = contUserParts });
                    activeContents = contContents;
                }
                else
                {
                    // Error before any content arrived (e.g. proxy reset on new
                    // connection) — retry the exact same request from scratch.
                    logger.LogWarning(
                        $"[Gemini] [Retry {attempt}/{AppConfig.MAX_RETRIES}] connection error, retrying");
                    activeContents     = geminiContentsOriginal;
                    partialTextToMerge = null;
                }
            }

            return null;
        }

        private static int ResolveThinkingBudget(string thinking, string effort)
        {
            if (string.Equals(thinking, "disabled", StringComparison.OrdinalIgnoreCase))
                return 0;

            string e = (effort ?? AppConfig.GEMINI_DEFAULT_EFFORT).ToLowerInvariant();
            return e switch
            {
                "none" => 0,          // explicit "no reasoning effort" — thinking off
                _      => -1,         // dynamic — model decides its own budget per-request
            };
        }

        private static JsonArray TranslateMessages(JsonArray claudeMessages)
        {
            var result = new JsonArray();

            foreach (JsonNode? msg in claudeMessages)
            {
                if (msg == null) continue;

                string    role    = msg["role"]?.GetValue<string>() ?? "";
                JsonNode? content = msg["content"];

                if (role == "user")
                {
                    if (IsToolResultTurn(content))
                    {
                        var parts = new JsonArray();
                        foreach (JsonNode? block in content!.AsArray())
                        {
                            if (block == null) continue;
                            if (block["type"]?.GetValue<string>() != "tool_result") continue;

                            string toolUseId = block["tool_use_id"]?.GetValue<string>() ?? "";
                            string name      = RecoverNameFromToolUseId(toolUseId);
                            string outputTxt = ExtractText(block["content"]);

                            parts.Add(new JsonObject
                            {
                                ["functionResponse"] = new JsonObject
                                {
                                    ["name"]     = name,
                                    ["response"] = new JsonObject { ["result"] = outputTxt },
                                },
                            });
                        }
                        if (parts.Count > 0)
                            result.Add(new JsonObject { ["role"] = "user", ["parts"] = parts });
                    }
                    else
                    {
                        result.Add(new JsonObject
                        {
                            ["role"]  = "user",
                            ["parts"] = TranslateUserParts(content),
                        });
                    }
                }
                else if (role == "assistant")
                {
                    JsonArray parts;

                    if (content is JsonArray blocks)
                    {
                        parts = ContentBlocksToGeminiParts(blocks);
                    }
                    else if (content is JsonValue strVal &&
                             strVal.TryGetValue<string>(out string? strContent))
                    {
                        parts = new JsonArray { new JsonObject { ["text"] = strContent ?? "" } };
                    }
                    else
                    {
                        parts = new JsonArray();
                    }

                    if (parts.Count > 0)
                        result.Add(new JsonObject { ["role"] = "model", ["parts"] = parts });
                }
                // Any other role is skipped (defensive).
            }

            return result;
        }

        private static JsonArray ContentBlocksToGeminiParts(JsonArray blocks)
        {
            var parts = new JsonArray();

            foreach (JsonNode? block in blocks)
            {
                string btype = block?["type"]?.GetValue<string>() ?? "";
                switch (btype)
                {
                    case "text":
                    {
                        string? txtSig = block!["thought_signature"]?.GetValue<string>();
                        var textPart = new JsonObject { ["text"] = block["text"]?.GetValue<string>() ?? "" };
                        if (!string.IsNullOrEmpty(txtSig))
                            textPart["thoughtSignature"] = txtSig;
                        parts.Add(textPart);
                        break;
                    }

                    case "tool_use":
                    {
                        string toolName = block!["name"]?.GetValue<string>() ?? "";
                        JsonNode? args  = block["input"]?.DeepClone() ?? new JsonObject();
                        string? sig     = block["thought_signature"]?.GetValue<string>();

                        var partObj = new JsonObject
                        {
                            ["functionCall"] = new JsonObject
                            {
                                ["name"] = toolName,
                                ["args"] = args,
                            },
                        };
                        if (!string.IsNullOrEmpty(sig))
                            partObj["thoughtSignature"] = sig;

                        parts.Add(partObj);
                        break;
                    }

                    // "thinking" blocks are intentionally skipped — Gemini has no
                    // equivalent replay mechanism wired up here.
                }
            }

            return parts;
        }

        /// <summary>Recover the original function name from a tool_use.id minted by this translator (see GeminiStreamParser).</summary>
        private static string RecoverNameFromToolUseId(string toolUseId)
        {
            int sep = toolUseId.IndexOf("__", StringComparison.Ordinal);
            return sep >= 0 && sep + 2 <= toolUseId.Length ? toolUseId[(sep + 2)..] : toolUseId;
        }

        /// <summary>True when the content array contains only tool_result blocks.</summary>
        private static bool IsToolResultTurn(JsonNode? content)
        {
            if (content is not JsonArray arr || arr.Count == 0) return false;
            return arr.All(n => n?["type"]?.GetValue<string>() == "tool_result");
        }

        /// <summary>Translate a user content payload to a Gemini "parts" array.</summary>
        private static JsonArray TranslateUserParts(JsonNode? content)
        {
            var parts = new JsonArray();

            if (content is JsonValue sv && sv.TryGetValue<string>(out string? plain))
            {
                parts.Add(new JsonObject { ["text"] = plain ?? "" });
                return parts;
            }

            if (content is JsonArray arr)
            {
                foreach (JsonNode? block in arr)
                {
                    string btype = block?["type"]?.GetValue<string>() ?? "";

                    if (btype == "text")
                    {
                        parts.Add(new JsonObject { ["text"] = block!["text"]?.GetValue<string>() ?? "" });
                    }
                    else if (btype == "image")
                    {
                        string mediaType = block!["source"]?["media_type"]?.GetValue<string>() ?? "image/png";
                        string data      = block["source"]?["data"]?.GetValue<string>() ?? "";

                        parts.Add(new JsonObject
                        {
                            ["inlineData"] = new JsonObject
                            {
                                ["mimeType"] = mediaType,
                                ["data"]     = data,
                            },
                        });
                    }
                    // Other block types (document, etc.) are skipped gracefully.
                }
                return parts;
            }

            parts.Add(new JsonObject { ["text"] = "" });
            return parts;
        }

        /// <summary>Extract a plain text string from a Claude content payload (string or array of text blocks).</summary>
        private static string ExtractText(JsonNode? content)
        {
            if (content == null) return "";

            if (content is JsonValue v && v.TryGetValue<string>(out string? s))
                return s ?? "";

            if (content is JsonArray arr)
            {
                var sb = new StringBuilder();
                foreach (JsonNode? block in arr)
                {
                    if (block?["type"]?.GetValue<string>() == "text")
                        sb.Append(block["text"]?.GetValue<string>() ?? "");
                }
                return sb.ToString();
            }

            return "";
        }

        private static string ExtractPartialText(JsonArray blocks) =>
            string.Concat(blocks
                .Where(b => b?["type"]?.GetValue<string>() == "text")
                .Select(b => b!["text"]?.GetValue<string>() ?? ""));

        private static void MergePartialText(JsonArray contentBlocks, string partialText)
        {
            if (string.IsNullOrEmpty(partialText)) return;

            foreach (JsonNode? block in contentBlocks)
            {
                if (block?["type"]?.GetValue<string>() == "text")
                {
                    string existing = block["text"]?.GetValue<string>() ?? "";
                    block["text"]   = partialText + existing;
                    return;
                }
            }

            // No text block in the continuation — add one at the front.
            contentBlocks.Insert(0, new JsonObject { ["type"] = "text", ["text"] = partialText });
        }

        /// <summary>Convert Claude tool definitions to Gemini functionDeclarations.</summary>
        private static JsonArray TranslateTools(JsonArray claudeTools)
        {
            var result = new JsonArray();

            foreach (JsonNode? tool in claudeTools)
            {
                if (tool == null) continue;
                if (tool["input_schema"] == null) continue; // Claude-native tool — skip

                string name = tool["name"]?.GetValue<string>()        ?? "";
                string desc = tool["description"]?.GetValue<string>() ?? "";

                JsonNode? parameters = tool["input_schema"]?.DeepClone();
                StripCacheControl(parameters);

                result.Add(new JsonObject
                {
                    ["name"]        = name,
                    ["description"] = desc,
                    ["parameters"]  = parameters
                                      ?? new JsonObject
                                      {
                                          ["type"]       = "object",
                                          ["properties"] = new JsonObject(),
                                      },
                });
            }

            return result;
        }

        /// <summary>Recursively remove "cache_control" keys from a JSON tree.</summary>
        private static void StripCacheControl(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                obj.Remove("cache_control");
                foreach (var kv in obj.ToList())
                    StripCacheControl(kv.Value);
            }
            else if (node is JsonArray arr)
            {
                foreach (JsonNode? child in arr)
                    StripCacheControl(child);
            }
        }

        /// <summary>Assemble the full JSON request body for one streamGenerateContent call.</summary>
        private static JsonObject BuildPayload(
            JsonArray contents,
            JsonArray geminiTools,
            string    systemPrompt,
            int       safeTokens,
            int       thinkingBudget)
        {
            var thinkingConfig = new JsonObject { ["thinkingBudget"] = thinkingBudget };
            if (thinkingBudget != 0)
                thinkingConfig["includeThoughts"] = true;

            var generationConfig = new JsonObject
            {
                ["maxOutputTokens"] = safeTokens,
                ["thinkingConfig"]  = thinkingConfig,
            };

            var payload = new JsonObject
            {
                ["contents"]         = contents.DeepClone().AsArray(),
                ["generationConfig"] = generationConfig,
            };

            if (!string.IsNullOrWhiteSpace(systemPrompt))
            {
                payload["system_instruction"] = new JsonObject
                {
                    ["parts"] = new JsonArray { new JsonObject { ["text"] = systemPrompt } },
                };
            }

            if (geminiTools.Count > 0)
            {
                payload["tools"] = new JsonArray
                {
                    new JsonObject { ["functionDeclarations"] = geminiTools.DeepClone().AsArray() },
                };
            }

            return payload;
        }

        private static string BuildUrl() =>
            $"{API_ROOT}{AppConfig.GEMINI_MODEL}:streamGenerateContent?alt=sse";

        private static readonly Lazy<HttpClient> _sharedProxyClient = new(() =>
        {
            var handler = new SocketsHttpHandler
            {
                Proxy                       = SystemProxy.CreateWebProxy(), // Windows system proxy + current Windows login (SSO)
                UseProxy                    = true,
                DefaultProxyCredentials     = CredentialCache.DefaultCredentials,
                ConnectTimeout              = TimeSpan.FromSeconds(AppConfig.CONNECT_TIMEOUT_SECONDS),
                PooledConnectionLifetime    = TimeSpan.FromMinutes(AppConfig.POOLED_CONNECTION_LIFETIME_MINUTES),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            };
            return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        });

        /// <summary>Extract a human-readable error message from a Gemini error body. Returns the raw body (truncated) if parsing fails.</summary>
        private static string TryExtractError(string body)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out JsonElement err) &&
                    err.TryGetProperty("message", out JsonElement msg))
                    return msg.GetString() ?? body;
            }
            catch { /* fall through */ }
            return body.Length > 300 ? body[..300] + "…" : body;
        }
    }
}
