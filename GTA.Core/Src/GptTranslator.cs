using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace GTA.Core
{

    public sealed class GptTranslator : ILlmClient
    {
        // ── GPT safety cap ────────────────────────────────────────────────────
        // gpt-6.1-sol supports up to 1 000 000 output tokens.
        private const int GPT_MAX_OUTPUT = 1_000_000;

        private const string RESPONSES_PATH = "/openai/v1/responses";

        // ─────────────────────────────────────────────────────────────────────

        /// <inheritdoc/>
        public string ProviderName => "GPT";

        /// <inheritdoc/>
        /// <remarks>Matches the section name expected in model_pricing.ini.</remarks>
        public string ModelName => AppConfig.GPT_MODEL;

        /// <inheritdoc/>
        public async Task<JsonObject?> CallAsync(
            JsonArray         messages,
            JsonArray         tools,
            string            systemPrompt,
            int               maxTokens,
            string            thinking,   // ignored for GPT (no separate thinking protocol)
            string            effort,     // → reasoning.effort  (none/low/medium/high/xhigh)
            bool              debug,
            IAgentLogger      logger,
            IStreamRenderer   renderer,
            CancellationToken ct = default)
        {
            logger.LastCallExceededContextLength = false;

            string url = BuildUrl();

            // ── Translate to OpenAI Responses API format ──────────────────────
            (JsonArray oaiInput, string? instructions) =
                TranslateMessages(messages, systemPrompt);
            JsonArray oaiTools   = TranslateTools(tools);
            int       safeTokens = Math.Min(maxTokens, GPT_MAX_OUTPUT);
 
            string gptEffort = (effort?.ToLowerInvariant()) switch
            {
                "none"    => "none",
                "minimal" => "minimal",
                "low"     => "low",
                "medium"  => "medium",
                "high"    => "high",
                "xhigh"   => "xhigh",
                "max"     => "xhigh",   // "max" → promote to highest GPT level
                _         => AppConfig.GPT_DEFAULT_EFFORT,
            };

            var payload = new JsonObject
            {
                ["model"]             = AppConfig.GPT_MODEL,
                ["input"]             = oaiInput,
                ["max_output_tokens"] = safeTokens,
                ["reasoning"]         = new JsonObject
                {
                    ["effort"]  = gptEffort,
                    ["summary"] = "auto",   // ← opt in to reasoning summary (thinking bubble)
                },
                ["stream"]            = true,
            };

            if (!string.IsNullOrWhiteSpace(instructions))
                payload["instructions"] = instructions;

            if (oaiTools.Count > 0)
                payload["tools"] = oaiTools;

            string payloadJson = payload.ToJsonString();

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

                try
                {
                    using var client  = CreateProxyClient(
                        TimeSpan.FromSeconds(AppConfig.STREAM_TIMEOUT_SECONDS));

                    HttpResponseMessage? response = null;

                    // "Connecting" phase begins here — see AzureApiClient for the
                    // full rationale of this block.
                    logger.OnConnectionWait(true, CancelLiveAttempt);
                    try
                    {
                        for (int connectAttempt = 1; connectAttempt <= AppConfig.MAX_CONNECT_RETRIES; connectAttempt++)
                        {
                            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            liveAttemptCts = attemptCts;

                            try
                            {
                                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                                request.Headers.TryAddWithoutValidation("api-key", AppConfig.AZURE_KEY);
                                request.Headers.Accept.Add(
                                    new MediaTypeWithQualityHeaderValue("text/event-stream"));
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
                                    logger.LogError($"[GPT] Giving up after {connectAttempt} connect attempts, each exceeding the {AppConfig.STREAM_TIMEOUT_SECONDS}s overall timeout.");
                                    throw new TimeoutException(
                                        $"[GPT] SendAsync exceeded the {AppConfig.STREAM_TIMEOUT_SECONDS}s overall timeout on all {connectAttempt} attempts.");
                                }
                                logger.LogInfo(wasManualRetry
                                    ? $"[GPT][ManualRetry] User clicked Retry — cancelling and starting a fresh connection (connect attempt {connectAttempt}/{AppConfig.MAX_CONNECT_RETRIES})."
                                    : $"[GPT] Retry because SendAsync exceeded the {AppConfig.STREAM_TIMEOUT_SECONDS}s overall timeout (connect attempt {connectAttempt}/{AppConfig.MAX_CONNECT_RETRIES}).");
                                // Manual retries skip the artificial delay.
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
                        throw new InvalidOperationException("[GPT] SendAsync produced no response and threw no exception — unexpected state.");

                    if (!response.IsSuccessStatusCode)
                    {
                        bool isTransient = response.StatusCode is
                            HttpStatusCode.ServiceUnavailable or   // 503
                            HttpStatusCode.BadGateway          or  // 502
                            HttpStatusCode.GatewayTimeout      or  // 504
                            (HttpStatusCode)429;                   // Too Many Requests

                        if (!isTransient || attempt >= AppConfig.MAX_RETRIES)
                        {
                            // Giving up — pay the cost of reading + printing the
                            // full error body only now, since it's the last word.
                            string errBody = await response.Content.ReadAsStringAsync(ct);
                            string errMsg   = TryExtractError(errBody);
                            // See LlmCallDiagnostics — lets AgenticLoop tell "prompt too
                            // long" apart from any other unrecoverable error.
                            logger.LastCallExceededContextLength =
                                LlmCallDiagnostics.LooksLikeContextLengthError(errMsg);
                            logger.LogError($"[GPT] HTTP {(int)response.StatusCode}: {errMsg}");
                            logger.LogError("[GPT] Non-retriable error. Giving up.");
                            return null;
                        }

                        // Quiet, compact retry line — no error-body dump.
                        logger.LogWarning(
                            $"[GPT] [Retry {attempt}/{AppConfig.MAX_RETRIES}] HTTP {(int)response.StatusCode}");
                        continue;
                    }

                    using Stream stream = await response.Content.ReadAsStreamAsync(ct);
                    JsonObject? result  = await ParseResponsesStreamAsync(stream, debug, renderer, logger, ct);

                    if (result != null)
                    {
                        // Log token usage + reasoning effort
                        if (debug)
                            logger.LogInfo(
                                $"[GPT] in={result["usage"]?["input_tokens"]} " +
                                $"out={result["usage"]?["output_tokens"]} " +
                                $"cached={result["usage"]?["cache_read_input_tokens"]} " +
                                $"reasoning.effort={gptEffort}");
                        return result;
                    }

                    // null = stream error; retry
                    if (attempt >= AppConfig.MAX_RETRIES)
                    {
                        logger.LogError($"[GPT] Max retries ({AppConfig.MAX_RETRIES}) reached.");
                        return null;
                    }
                    logger.LogWarning($"[GPT] [Retry {attempt}/{AppConfig.MAX_RETRIES}] stream error");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // Quiet by default: full exception detail only in debug mode
                    // or once we're truly out of retries and giving up.
                    if (debug || attempt >= AppConfig.MAX_RETRIES)
                    {
                        logger.LogError($"[GPT] {ex.Message}");
                        if (ex.InnerException != null)
                            logger.LogError($"[GPT] Inner: {ex.InnerException.Message}");
                    }

                    if (attempt >= AppConfig.MAX_RETRIES)
                    {
                        logger.LogError("[GPT] Max retries reached. Giving up.");
                        return null;
                    }
                }
            }

            return null;
        }

        private static (JsonArray input, string? instructions) TranslateMessages(
            JsonArray claudeMessages, string systemPrompt)
        {
            var result = new JsonArray();

            // System prompt goes to the top-level "instructions" field, not the input array.
            string? instructions = string.IsNullOrWhiteSpace(systemPrompt) ? null : systemPrompt;

            foreach (JsonNode? msg in claudeMessages)
            {
                if (msg == null) continue;

                string    role    = msg["role"]?.GetValue<string>() ?? "";
                JsonNode? content = msg["content"];

                if (role == "user")
                {
                    if (IsToolResultTurn(content))
                    {
                        foreach (JsonNode? block in content!.AsArray())
                        {
                            if (block == null) continue;
                            if (block["type"]?.GetValue<string>() != "tool_result") continue;

                            // call_id in Responses API matches tool_use_id from Claude
                            string callId  = block["tool_use_id"]?.GetValue<string>() ?? "";
                            string output  = ExtractText(block["content"]);

                            result.Add(new JsonObject
                            {
                                ["type"]    = "function_call_output",
                                ["call_id"] = callId,
                                ["output"]  = output,
                            });
                        }
                    }
                    else
                    {
                        // ── Regular user message (text ± images) ──────────────
                        result.Add(TranslateUserMessage(content));
                    }
                }
                else if (role == "assistant")
                {
                    string           textAccum     = "";
                    List<JsonObject> reasoningItems = new();
                    List<JsonObject> funcCallItems  = new();

                    if (content is JsonArray blocks)
                    {
                        foreach (JsonNode? block in blocks)
                        {
                            string btype = block?["type"]?.GetValue<string>() ?? "";
                            switch (btype)
                            {
                                case "text":
                                    textAccum += block!["text"]?.GetValue<string>() ?? "";
                                    break;

                                case "thinking":
                                    string thinkingText = block!["thinking"]?.GetValue<string>()  ?? "";
                                    string reasoningId  = block["signature"]?.GetValue<string>()  ?? "";

                                    if (!string.IsNullOrEmpty(reasoningId))
                                    {
                                        reasoningItems.Add(new JsonObject
                                        {
                                            ["id"]   = reasoningId,
                                            ["type"] = "reasoning",
                                            ["summary"] = new JsonArray
                                            {
                                                new JsonObject
                                                {
                                                    ["type"] = "summary_text",
                                                    ["text"] = thinkingText,
                                                }
                                            },
                                        });
                                    }
                                    break;

                                case "tool_use":
                                    // Claude tool_use.id is used as call_id (the correlation key
                                    // that links function_call → function_call_output).
                                    string toolId   = block!["id"]?.GetValue<string>()  ?? "";
                                    string toolName = block["name"]?.GetValue<string>() ?? "";
                                    string argsJson = block["input"]?.ToJsonString()    ?? "{}";

                                    funcCallItems.Add(new JsonObject
                                    {
                                        ["type"]      = "function_call",
                                        ["call_id"]   = toolId,
                                        ["name"]      = toolName,
                                        ["arguments"] = argsJson,
                                    });
                                    break;
                            }
                        }
                    }
                    else if (content is JsonValue strVal &&
                             strVal.TryGetValue<string>(out string? strContent))
                    {
                        textAccum = strContent ?? "";
                    }

                    // Emit in the original output order:
                    // 1. reasoning items (GPT emits reasoning before message/function_calls)
                    foreach (JsonObject rItem in reasoningItems)
                        result.Add(rItem);

                    // 2. Text message item (if any)
                    if (!string.IsNullOrEmpty(textAccum))
                    {
                        result.Add(new JsonObject
                        {
                            ["role"]    = "assistant",
                            ["content"] = textAccum,
                        });
                    }

                    // 3. Function_call items (if any)
                    foreach (JsonObject fcItem in funcCallItems)
                        result.Add(fcItem);
                }
                // Any other role is skipped (defensive).
            }

            return (result, instructions);
        }

        /// <summary>True when the content array contains only tool_result blocks</summary>
        private static bool IsToolResultTurn(JsonNode? content)
        {
            if (content is not JsonArray arr || arr.Count == 0) return false;
            return arr.All(n => n?["type"]?.GetValue<string>() == "tool_result");
        }

        /// <summary>Translate a user content payload to an OpenAI Responses API "user" role message.</summary>
        private static JsonObject TranslateUserMessage(JsonNode? content)
        {
            // String shorthand
            if (content is JsonValue sv && sv.TryGetValue<string>(out string? plain))
                return new JsonObject { ["role"] = "user", ["content"] = plain ?? "" };

            // Block array
            if (content is JsonArray arr)
            {
                bool hasRichContent = arr.Any(n =>
                {
                    string t = n?["type"]?.GetValue<string>() ?? "";
                    return t != "text" && t != "";
                });

                if (!hasRichContent)
                {
                    // All text — collapse to a single string (simpler and cheaper)
                    return new JsonObject
                    {
                        ["role"]    = "user",
                        ["content"] = ExtractText(content),
                    };
                }

                // Rich content — build Responses API multi-part content array
                var richArr = new JsonArray();

                foreach (JsonNode? block in arr)
                {
                    string btype = block?["type"]?.GetValue<string>() ?? "";

                    if (btype == "text")
                    {
                        // Responses API text part: type = "input_text" (not "text")
                        richArr.Add(new JsonObject
                        {
                            ["type"] = "input_text",
                            ["text"] = block!["text"]?.GetValue<string>() ?? "",
                        });
                    }
                    else if (btype == "image")
                    {
                        string mediaType = block!["source"]?["media_type"]?.GetValue<string>()
                                          ?? "image/png";
                        string data      = block["source"]?["data"]?.GetValue<string>() ?? "";

                        richArr.Add(new JsonObject
                        {
                            ["type"]      = "input_image",
                            ["image_url"] = $"data:{mediaType};base64,{data}",
                        });
                    }
                    // Other block types (document, etc.) are skipped gracefully.
                }

                return new JsonObject { ["role"] = "user", ["content"] = richArr };
            }

            // Fallback
            return new JsonObject { ["role"] = "user", ["content"] = "" };
        }

        /// <summary>Extract a plain text string from a Claude content payload.</summary>
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

        /// <summary>Convert Claude tool definitions to OpenAI Responses API function format.</summary>
        private static JsonArray TranslateTools(JsonArray claudeTools)
        {
            var result = new JsonArray();

            foreach (JsonNode? tool in claudeTools)
            {
                if (tool == null) continue;

                // Skip Claude-native tools — heuristic: proper function tools have input_schema.
                if (tool["input_schema"] == null) continue;

                string name = tool["name"]?.GetValue<string>()        ?? "";
                string desc = tool["description"]?.GetValue<string>() ?? "";

                // Deep-clone so we don't mutate the shared schema, then strip cache_control.
                JsonNode? parameters = tool["input_schema"]?.DeepClone();
                StripCacheControl(parameters);

                // Responses API uses internally-tagged format (no "function" wrapper).
                result.Add(new JsonObject
                {
                    ["type"]        = "function",
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

        private static async Task<JsonObject?> ParseResponsesStreamAsync(
            Stream            stream,
            bool              debug,
            IStreamRenderer   renderer,
            IAgentLogger      logger,
            CancellationToken ct)
        {
            var reasoningBlocks = new Dictionary<int, (string itemId, StringBuilder text)>();

            // ── Text block state: output_index → accumulated text ─────────────
            var textBlocks = new Dictionary<int, StringBuilder>();

            var funcCalls = new Dictionary<int,
                (string itemId, string callId, string name, StringBuilder args)>();

            // ── Result metadata ───────────────────────────────────────────────
            string stopReason   = "end_turn";
            int    inputTokens  = 0;
            int    outputTokens = 0;
            int    cachedTokens = 0;

            bool wasInterrupted = false;

            try
            {
                using var reader = new StreamReader(stream, Encoding.UTF8);

                string? line;
                while ((line = await reader.ReadLineAsync(ct)) != null)
                {
                    if (!line.StartsWith("data:")) continue;

                    string data = line["data:".Length..].Trim();
                    if (data == "[DONE]") break;

                    JsonNode? node;
                    try   { node = JsonNode.Parse(data); }
                    catch { continue; }
                    if (node == null) continue;

                    string eventType = node["type"]?.GetValue<string>() ?? "";

                    switch (eventType)
                    {
                        // ── New output item ───────────────────────────────────
                        case "response.output_item.added":
                        {
                            int    outIdx  = node["output_index"]?.GetValue<int>() ?? 0;
                            string itmType = node["item"]?["type"]?.GetValue<string>() ?? "";

                            if (itmType == "message")
                            {
                                // Text output item — open a text block
                                textBlocks[outIdx] = new StringBuilder();
                                await renderer.OnBlockStart(outIdx, "text", "", "");
                            }
                            else if (itmType == "function_call")
                            {
                                string itemId = node["item"]?["id"]?.GetValue<string>()      ?? "";
                                string callId = node["item"]?["call_id"]?.GetValue<string>() ?? "";
                                string fnName = node["item"]?["name"]?.GetValue<string>()    ?? "";

                                funcCalls[outIdx] = (itemId, callId, fnName, new StringBuilder());

                                // Pass callId as the tool id so the renderer shows the right id
                                await renderer.OnBlockStart(outIdx, "tool_use", callId, fnName);
                            }
                            else if (itmType == "reasoning")
                            {
                                // Reasoning item — open a thinking block for the bubble
                                // item.id = the reasoning item identifier (rs_xxx)
                                string itemId = node["item"]?["id"]?.GetValue<string>() ?? "";
                                reasoningBlocks[outIdx] = (itemId, new StringBuilder());
                                await renderer.OnBlockStart(outIdx, "thinking", "", "");
                            }
                            // Other item types are silently skipped.
                            break;
                        }

                        // ── Text chunk ────────────────────────────────────────
                        case "response.output_text.delta":
                        {
                            int    outIdx = node["output_index"]?.GetValue<int>() ?? 0;
                            string delta  = node["delta"]?.GetValue<string>()     ?? "";

                            if (!string.IsNullOrEmpty(delta))
                            {
                                if (textBlocks.TryGetValue(outIdx, out var sb))
                                    sb.Append(delta);
                                renderer.OnTextChunk(outIdx, delta);
                            }
                            break;
                        }

                        // ── Text finalised — authoritative full text ──────────
                        case "response.output_text.done":
                        {
                            int     outIdx   = node["output_index"]?.GetValue<int>() ?? 0;
                            string? doneText = node["text"]?.GetValue<string>();

                            // Replace the accumulated delta-text with the canonical value.
                            if (doneText != null && textBlocks.TryGetValue(outIdx, out var sb))
                            {
                                sb.Clear();
                                sb.Append(doneText);
                            }
                            break;
                        }

                        // ── Tool argument chunk ───────────────────────────────
                        case "response.function_call_arguments.delta":
                        {
                            int    outIdx = node["output_index"]?.GetValue<int>() ?? 0;
                            string delta  = node["delta"]?.GetValue<string>()     ?? "";

                            if (!string.IsNullOrEmpty(delta) &&
                                funcCalls.TryGetValue(outIdx, out var fc))
                            {
                                fc.args.Append(delta);
                                renderer.OnToolInputChunk(outIdx, fc.name, delta);
                            }
                            break;
                        }

                        // ── Tool arguments finalised ──────────────────────────
                        case "response.function_call_arguments.done":
                        {
                            int outIdx = node["output_index"]?.GetValue<int>() ?? 0;

                            // "arguments" can live directly on the event OR inside "item".
                            string? doneArgs =
                                node["arguments"]?.GetValue<string>()
                                ?? node["item"]?["arguments"]?.GetValue<string>();

                            if (doneArgs != null && funcCalls.TryGetValue(outIdx, out var fc))
                            {
                                fc.args.Clear();
                                fc.args.Append(doneArgs);
                            }
                            break;
                        }

                        // ── Reasoning summary chunk (the "thinking bubble" text) ──
                        case "response.reasoning_summary_text.delta":
                        {
                            int    outIdx = node["output_index"]?.GetValue<int>() ?? 0;
                            string delta  = node["delta"]?.GetValue<string>()     ?? "";

                            if (!string.IsNullOrEmpty(delta) &&
                                reasoningBlocks.TryGetValue(outIdx, out var rb))
                            {
                                rb.text.Append(delta);
                                renderer.OnThinkingChunk(outIdx, delta);
                            }
                            break;
                        }

                        // ── Reasoning summary finalised ───────────────────────────
                        case "response.reasoning_summary_text.done":
                        {
                            int     outIdx   = node["output_index"]?.GetValue<int>() ?? 0;
                            string? doneText = node["text"]?.GetValue<string>();

                            if (doneText != null &&
                                reasoningBlocks.TryGetValue(outIdx, out var rb))
                            {
                                rb.text.Clear();
                                rb.text.Append(doneText);
                            }
                            break;
                        }

                        case "response.output_item.done":
                        {
                            if (node["item"]?["type"]?.GetValue<string>() == "reasoning")
                            {
                                int outIdx = node["output_index"]?.GetValue<int>() ?? 0;

                                if (reasoningBlocks.TryGetValue(outIdx, out var rb) &&
                                    rb.text.Length == 0)
                                {
                                    // Safe array access — summary may be [] during early stream events
                                    var summaryArr = node["item"]?["summary"] as JsonArray;
                                    string? summaryText = summaryArr != null && summaryArr.Count > 0
                                        ? summaryArr[0]?["text"]?.GetValue<string>()
                                        : null;

                                    if (!string.IsNullOrEmpty(summaryText))
                                    {
                                        rb.text.Append(summaryText);
                                        renderer.OnThinkingChunk(outIdx, summaryText!);
                                    }
                                }
                            }
                            break;
                        }

                        // ── Stream complete ───────────────────────────────────
                        case "response.completed":
                        case "response.incomplete":
                        {
                            JsonNode? resp = node["response"];
                            if (resp != null)
                            {
                                string status = resp["status"]?.GetValue<string>() ?? "completed";

                                if (status == "incomplete")
                                {
                                    string reason =
                                        resp["incomplete_details"]?["reason"]?.GetValue<string>() ?? "";
                                    stopReason = reason.Contains("max_output_tokens")
                                        ? "max_tokens"
                                        : "end_turn";
                                }
                                else
                                {
                                    // Inspect the output array to detect tool calls.
                                    bool hasFuncCall = false;
                                    if (resp["output"] is JsonArray outArr)
                                    {
                                        foreach (JsonNode? item in outArr)
                                        {
                                            if (item?["type"]?.GetValue<string>() == "function_call")
                                            {
                                                hasFuncCall = true;
                                                break;
                                            }
                                        }
                                    }
                                    stopReason = hasFuncCall ? "tool_use" : "end_turn";
                                }

                                // Parse usage — Responses API supplies these in the response object.
                                if (resp["usage"] is JsonNode usageNode)
                                {
                                    inputTokens  = usageNode["input_tokens"]?.GetValue<int>()
                                                   ?? inputTokens;
                                    outputTokens = usageNode["output_tokens"]?.GetValue<int>()
                                                   ?? outputTokens;

                                    // Cached tokens (field name may vary across API versions)
                                    cachedTokens =
                                        usageNode["input_tokens_details"]?["cached_tokens"]?.GetValue<int>()
                                        ?? usageNode["prompt_tokens_details"]?["cached_tokens"]?.GetValue<int>()
                                        ?? cachedTokens;
                                }
                            }
                            break;
                        }

                        // ── Response failed ───────────────────────────────────
                        case "response.failed":
                        {
                            string errMsg =
                                node["response"]?["error"]?["message"]?.GetValue<string>()
                                ?? "Unknown failure";
                            // Quiet by default — GptTranslator's retry loop already
                            // logs one concise "[Retry n/N] stream error" warning line.
                            if (debug) renderer.OnLogLine($"\n  [GPT FAILED] {errMsg}");
                            wasInterrupted = true;
                            break;
                        }

                        // ── Protocol-level error ──────────────────────────────
                        case "error":
                        {
                            string errMsg = node["message"]?.GetValue<string>() ?? "Unknown error";
                            if (debug) renderer.OnLogLine($"\n  [GPT ERROR] {errMsg}");
                            wasInterrupted = true;
                            break;
                        }

                        // ── Known-but-ignored events ──────────────────────────
                        case "response.created":
                        case "response.in_progress":
                        case "response.content_part.added":
                        case "response.content_part.done":
                        case "response.reasoning_summary_part.added":  // structural, text via .delta
                        case "response.reasoning_summary_part.done":   // structural, text via .done
                        case "response.refusal.delta":
                        case "response.refusal.done":
                            break;

                        // ── Unknown event (debug only) ────────────────────────
                        default:
                            if (debug && !string.IsNullOrEmpty(eventType))
                                renderer.OnLogLine(
                                    $"[GPT DEBUG] type=\"{eventType}\" " +
                                    $"raw={data[..Math.Min(200, data.Length)]}");
                            break;
                    }

                    // The stream is finished after these terminal events.
                    if (eventType is "response.completed" or "response.incomplete"
                                  or "response.failed")
                        break;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Quiet by default — the outer retry loop already logs one
                // concise "[Retry n/N] stream error" warning line per attempt.
                if (debug)
                {
                    renderer.OnLogLine($"\n  [GPT STREAM ERROR] {ex.Message}");
                    if (ex.InnerException != null)
                        renderer.OnLogLine($"  [GPT STREAM ERROR] Inner: {ex.InnerException.Message}");
                }
                wasInterrupted = true;
            }

            if (wasInterrupted)
            {
                foreach (var fc in funcCalls.Values)
                    if (!string.IsNullOrEmpty(fc.callId))
                        logger.LogToolAbandoned(fc.callId,
                            "connection interrupted mid-call before this tool actually ran; " +
                            "retried automatically.");

                return null;
            }

            // ── Fire OnBlockStop and reconstruct Claude-format content blocks ──
            var contentBlocks = new JsonArray();

            // Merge all output indices (reasoning, text, function calls) in ascending order.
            var allIndices = new SortedSet<int>(reasoningBlocks.Keys);
            foreach (int k in textBlocks.Keys) allIndices.Add(k);
            foreach (int k in funcCalls.Keys)  allIndices.Add(k);

            foreach (int idx in allIndices)
            {
                // ── Reasoning / thinking block ────────────────────────────────
                if (reasoningBlocks.TryGetValue(idx, out var rb))
                {
                    string thinkingText = rb.text.ToString();
                    renderer.OnBlockStop(idx, "thinking", thinkingText.Length);

                    contentBlocks.Add(new JsonObject
                    {
                        ["type"]      = "thinking",
                        ["thinking"]  = thinkingText,
                        ["signature"] = rb.itemId,   // item ID (e.g. "rs_6abc…")
                    });
                }
                // ── Text block ────────────────────────────────────────────────
                else if (textBlocks.TryGetValue(idx, out var textSb))
                {
                    renderer.OnBlockStop(idx, "text", textSb.Length);
                    contentBlocks.Add(new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = textSb.ToString(),
                    });
                }
                // ── Tool call block ───────────────────────────────────────────
                else if (funcCalls.TryGetValue(idx, out var fc))
                {
                    string argsJson = fc.args.ToString();
                    renderer.OnBlockStop(idx, "tool_use", argsJson.Length);

                    JsonNode? inputNode;
                    try
                    {
                        inputNode = string.IsNullOrWhiteSpace(argsJson)
                            ? new JsonObject()
                            : JsonNode.Parse(argsJson);
                    }
                    catch { inputNode = new JsonObject(); }

                    // Use callId as the Claude tool_use.id so that AgenticLoop matches it with
                    // tool_result.tool_use_id → translated back to function_call_output.call_id.
                    contentBlocks.Add(new JsonObject
                    {
                        ["type"]  = "tool_use",
                        ["id"]    = fc.callId,
                        ["name"]  = fc.name,
                        ["input"] = inputNode ?? new JsonObject(),
                    });
                }
            }

            int freshInputTokens = Math.Max(0, inputTokens - cachedTokens);

            // ── Return Claude-format response object ──────────────────────
            return new JsonObject
            {
                ["stop_reason"] = stopReason,
                ["content"]     = contentBlocks,
                ["usage"] = new JsonObject
                {
                    ["input_tokens"]                = freshInputTokens,
                    ["output_tokens"]               = outputTokens,
                    ["cache_creation_input_tokens"] = 0,            // N/A for GPT
                    ["cache_read_input_tokens"]     = cachedTokens, // automatic cache hits
                },
            };
        }

        private static string BuildUrl() =>
            AppConfig.GPT_BASE.TrimEnd('/') + RESPONSES_PATH;

        /// <summary>Create an HttpClient using the corporate proxy configuration.</summary>
        private static HttpClient CreateProxyClient(TimeSpan timeout)
        {
            var handler = new HttpClientHandler
            {
                Proxy    = SystemProxy.CreateWebProxy(), // Windows system proxy + current Windows login (SSO)
                UseProxy = true,
                DefaultProxyCredentials = CredentialCache.DefaultCredentials,
            };
            return new HttpClient(handler) { Timeout = timeout };
        }

        /// <summary>Extract a human-readable error message from an Azure OpenAI error body.</summary>
        private static string TryExtractError(string body)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(body);
                // Azure OpenAI error shape: { "error": { "message": "..." } }
                if (doc.RootElement.TryGetProperty("error", out JsonElement err) &&
                    err.TryGetProperty("message", out JsonElement msg))
                    return msg.GetString() ?? body;
            }
            catch { /* fall through */ }
            return body.Length > 300 ? body[..300] + "…" : body;
        }
    }
}
