using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace GTA.Core
{
    public static class AzureApiClient
    {
        // Tools whose (big) string inputs we want streamed live to the UI
        // (see FileToolCard). Sent with eager_input_streaming = true.
        private static readonly HashSet<string> EagerStreamingTools = new(StringComparer.Ordinal)
        {
            "write_file", "append_to_file", "edit_line", "insert_line", "replace_in_file", "remove_line",
        };

        /// <summary>
        /// Marker key a caller can set (to true) on one message object. BuildPayload turns it into an extra
        /// prompt-cache breakpoint on that message's last block, then removes the key.
        /// Why: Anthropic refreshes the 5-minute cache TTL of EVERY explicit breakpoint on every request.
        /// A dispatched Sub Agent can run 40+ minutes. Without a pin, the parent's conversation prefix
        /// expires during that time and the parent re-writes its whole history after the Sub Agent reports.
        /// </summary>
        public const string CachePinKey = "_cache_pin";

        // Set once if the API answers 400 because of eager_input_streaming.
        private static volatile bool _eagerStreamingRejected;

        // Set once if the API answers 400 because of the 1-hour cache TTL ("ttl": "1h"). Then we stay on 5 minutes.
        private static volatile bool _ttl1hRejected;

        /// <summary>A fresh cache_control object (a JsonNode can only have ONE parent, so never share one instance).</summary>
        private static JsonObject MakeCacheControl(bool ttl1h)
        {
            var cc = new JsonObject { ["type"] = "ephemeral" };
            if (ttl1h) cc["ttl"] = "1h";
            return cc;
        }
        public static async Task<JsonObject?> CallAzureAsync(
            JsonArray         messages,
            JsonArray         tools,
            string            systemPrompt,
            int               maxTokens,
            string            thinking,
            string            effort,
            bool              debug,
            IAgentLogger      logger,
            IStreamRenderer   renderer,
            CancellationToken ct = default)
        {
            logger.LastCallExceededContextLength = false;

            string url = AppConfig.AZURE_BASE.TrimEnd('/') + "/v1/messages";

            JsonArray activeMessages = messages;

            Dictionary<int, string>? partialTexts = null;

            var timeStamp = System.Diagnostics.Stopwatch.GetTimestamp();

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

                SseParseResult? parseResult = null;
                bool            ttl1hSent   = false;   // did THIS attempt's request carry the 1-hour cache TTL?

                try
                {
                    bool eagerSent = !_eagerStreamingRejected;
                    ttl1hSent = AppConfig.CACHE_TTL_1H && !_ttl1hRejected;
                    // The body is STREAMED message by message into the request (no byte[] of the whole history, which
                    // for a chat with screenshots is tens of MB and sits in the Large Object Heap). The length is
                    // counted in a first pass that writes nothing, so the request still has a normal Content-Length.
                    JsonObject payloadNode = BuildPayload(activeMessages, tools, systemPrompt, maxTokens, thinking, effort, eagerSent, ttl1hSent);
                    long payloadBytes = MeasurePayload(payloadNode);

                    
                    HttpClient client = _sharedProxyClient.Value; // shared/reused — do NOT dispose

                    timeStamp = System.Diagnostics.Stopwatch.GetTimestamp();
                    logger.LogInfo($"Starting SendAsync (payload: {payloadBytes / 1024.0:F1} KB)");
                    // ResponseHeadersRead: SendAsync returns as soon as HTTP headers
                    // arrive.  We then read the SSE body ourselves line by line.
                    HttpResponseMessage? response = null;

                    logger.OnConnectionWait(true, CancelLiveAttempt);
                    try
                    {
                    for (int connectAttempt = 1; connectAttempt <= AppConfig.MAX_CONNECT_RETRIES; connectAttempt++)
                    {
                        var connectAttemptSw = System.Diagnostics.Stopwatch.StartNew();
                        logger.LogInfo($"[ConnectAttempt {connectAttempt}/{AppConfig.MAX_CONNECT_RETRIES}] Starting SendAsync (connect + write body + wait for response headers)...");

                        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        attemptCts.CancelAfter(TimeSpan.FromSeconds(AppConfig.HEADERS_TIMEOUT_SECONDS));
                        liveAttemptCts = attemptCts;

                        try
                        {
                            using var request = new HttpRequestMessage(HttpMethod.Post, url);
                            request.Options.Set(LoggerOptionKey, logger);
                            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AppConfig.AZURE_KEY);
                            request.Headers.Add("anthropic-version", "2023-06-01");
                            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                            request.Content = new TimedJsonContent(payloadNode, payloadBytes, logger);

                            response = await client.SendAsync(
                                request, HttpCompletionOption.ResponseHeadersRead, attemptCts.Token);
                            logger.LogInfo($"[ConnectAttempt {connectAttempt}/{AppConfig.MAX_CONNECT_RETRIES}] Response headers received after {connectAttemptSw.Elapsed.TotalMilliseconds:F0}ms total for THIS attempt (connect + upload + wait-for-headers combined).");
                            break; // got a response (success or non-2xx) — stop retrying connect
                        }
                        catch (HttpRequestException reqEx) when (reqEx.InnerException is IOException)
                        {
                            if (connectAttempt >= AppConfig.MAX_CONNECT_RETRIES)
                            {
                                logger.LogInfo($"Giving up after {connectAttempt} connect-level IOException retries (last attempt ran {connectAttemptSw.Elapsed.TotalMilliseconds:F0}ms).");
                                throw;
                            }
                            logger.LogInfo(
                                $"Retry because HttpClient caught IOException after {connectAttemptSw.Elapsed.TotalMilliseconds:F0}ms (this attempt). (connect attempt {connectAttempt}/{AppConfig.MAX_CONNECT_RETRIES})");
                            await Task.Delay(AppConfig.CONNECT_RETRY_DELAY_MS, ct);
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            bool wasManualRetry = manualRetryRequested;
                            manualRetryRequested = false;

                            if (connectAttempt >= AppConfig.MAX_CONNECT_RETRIES)
                            {
                                logger.LogInfo($"Giving up after {connectAttempt} attempts (last attempt ran {connectAttemptSw.Elapsed.TotalMilliseconds:F0}ms), each exceeding the {AppConfig.HEADERS_TIMEOUT_SECONDS}s headers-wait timeout.");
                                throw new TimeoutException(
                                    $"SendAsync exceeded the {AppConfig.HEADERS_TIMEOUT_SECONDS}s headers-wait timeout on all {connectAttempt} attempts.");
                            }
                            logger.LogInfo(wasManualRetry
                                ? $"[ManualRetry] User clicked Retry after {connectAttemptSw.Elapsed.TotalMilliseconds:F0}ms — cancelling and starting a fresh connection (connect attempt {connectAttempt}/{AppConfig.MAX_CONNECT_RETRIES})."
                                : $"Retry because SendAsync exceeded the {AppConfig.HEADERS_TIMEOUT_SECONDS}s headers-wait timeout after {connectAttemptSw.Elapsed.TotalMilliseconds:F0}ms (this attempt). (connect attempt {connectAttempt}/{AppConfig.MAX_CONNECT_RETRIES})");
                            // Manual retries skip the artificial delay — the user already
                            // waited long enough and wants a new attempt right now.
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
                        throw new InvalidOperationException("SendAsync produced no response and threw no exception — unexpected state.");
                    

                    var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(timeStamp);
                    logger.LogInfo($"Http call took: {elapsed.TotalMilliseconds}ms. | StatusCode: {response.StatusCode}");

                    if (!response.IsSuccessStatusCode)
                    {
                        if ((eagerSent || ttl1hSent) && response.StatusCode == HttpStatusCode.BadRequest)
                        {
                            string badBody = await response.Content.ReadAsStringAsync();

                            // Check the 1-hour TTL FIRST: its error text can also say "Extra inputs are not permitted".
                            if (ttl1hSent &&
                                (badBody.Contains("ttl", StringComparison.OrdinalIgnoreCase) ||
                                 badBody.Contains("cache_control", StringComparison.OrdinalIgnoreCase)))
                            {
                                _ttl1hRejected = true;
                                logger.LogWarning("[cache ttl 1h] rejected by the API — falling back to the 5-minute cache TTL for the rest of this run. " +
                                                  "(Server said: " + TryExtractError(badBody) + ")");
                                attempt--;       // do not burn a retry for this
                                continue;
                            }

                            if (eagerSent &&
                                (badBody.Contains("eager_input_streaming", StringComparison.OrdinalIgnoreCase) ||
                                 badBody.Contains("Extra inputs are not permitted", StringComparison.OrdinalIgnoreCase)))
                            {
                                _eagerStreamingRejected = true;
                                logger.LogWarning("[eager_input_streaming] rejected by the API — retrying without live tool-input streaming.");
                                attempt--;       // do not burn a retry for this
                                continue;
                            }
                        }
                        bool isTransient = response.StatusCode is
                            HttpStatusCode.RequestTimeout      or  // 408
                            HttpStatusCode.ServiceUnavailable  or  // 503
                            HttpStatusCode.BadGateway          or  // 502
                            HttpStatusCode.GatewayTimeout      or  // 504
                            (HttpStatusCode)429;                   // Too Many Requests

                        if (!isTransient || attempt >= AppConfig.MAX_RETRIES)
                        {
                            string errBody = await response.Content.ReadAsStringAsync();
                            string errMsg  = TryExtractError(errBody);
                            logger.LastCallExceededContextLength =
                                LlmCallDiagnostics.LooksLikeContextLengthError(errMsg);
                            logger.LogError($"Azure {(int)response.StatusCode}: {errMsg}");
                            logger.LogError("Non-retriable error or max retries reached. Giving up.");
                            return null;
                        }

                        logger.LogWarning(
                            $"[Retry {attempt}/{AppConfig.MAX_RETRIES}] HTTP {(int)response.StatusCode}");

                        // No partial text to preserve — retry the exact same request.
                        activeMessages = messages;
                        partialTexts   = null;
                        continue; // go to next attempt
                    }

                    using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    streamCts.CancelAfter(TimeSpan.FromSeconds(AppConfig.STREAM_TIMEOUT_SECONDS));
                    using Stream stream = await response.Content.ReadAsStreamAsync(streamCts.Token);
                    parseResult = await SseStreamParser.ParseAsync(
                        stream, debug, renderer, streamCts.Token,
                        onActivity: () => streamCts.CancelAfter(TimeSpan.FromSeconds(AppConfig.STREAM_TIMEOUT_SECONDS)));
                }
                catch (Exception ex)
                {
                    if (debug || attempt >= AppConfig.MAX_RETRIES)
                    {
                        logger.LogError(ex.Message);
                        if (ex.InnerException != null)
                            logger.LogError($"Inner: {ex.InnerException.Message}");
                    }

                    var exMsg = $"{ex.Message} ({ex.GetType().Name})";
                    if (ex.InnerException != null) exMsg = $"{exMsg} | InnerMsg: {ex.InnerException.Message} ({ex.InnerException.GetType().Name})";

                    var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(timeStamp);
                    logger.LogInfo($"Http call error. Took: {elapsed.TotalMilliseconds}ms. | Msg: {exMsg}");
                }
                // STOP button: the catch above swallows every exception (it is the retry logic), and the stream parser
                // may return a half result when it is cancelled. Without this line a stop was handled like a network
                // error: retry, or "return null" (= the loop treats it as a failed call). Always stop for real.
                ct.ThrowIfCancellationRequested();


                // ── Success ───────────────────────────────────────────────────────
                if (parseResult != null && !parseResult.WasInterrupted)
                {
                    JsonArray contentBlocks = SseStreamParser.ReconstructBlocks(parseResult);

                    // If this was a continuation retry, merge the partial text that
                    // was already printed before the error with the new continuation.
                    if (partialTexts != null)
                        SseStreamParser.MergePartialTexts(contentBlocks, partialTexts);

                    // 1-hour TTL bookkeeping (for pricing + the round badge in the UI).
                    int  write   = parseResult.CacheCreationInputTokens;
                    int  write1h = parseResult.CacheCreation1hInputTokens;
                    // Request carried ttl=1h and was accepted, but the API sent no 5m/1h split: treat every write as 1h.
                    if (ttl1hSent && !parseResult.CacheCreationBreakdownSeen) write1h = write;
                    write1h = Math.Clamp(write1h, 0, write);
                    // The server wrote tokens but NONE as 1h although we asked for 1h => it silently ignored the TTL.
                    bool ttl1hUsed = ttl1hSent && !(parseResult.CacheCreationBreakdownSeen && write > 0 && write1h == 0);
                    logger.LogInfo(
                        $"[cache] ttl requested={(ttl1hSent ? "1h" : "5m")} | write={write:N0} (1h part={write1h:N0}, " +
                        $"split reported by API={parseResult.CacheCreationBreakdownSeen}) | read={parseResult.CacheReadInputTokens:N0}" +
                        (ttl1hSent && !ttl1hUsed ? " | WARNING: API wrote the cache as 5m although 1h was requested" : ""));

                    return new JsonObject
                    {
                        ["stop_reason"] = parseResult.StopReason,
                        ["content"]     = contentBlocks,
                        // Embed token usage so AgenticLoop can accumulate and log it.
                        ["usage"]       = new JsonObject
                        {
                            ["input_tokens"]                   = parseResult.InputTokens,
                            ["output_tokens"]                  = parseResult.OutputTokens,
                            ["cache_creation_input_tokens"]    = write,
                            ["cache_creation_1h_input_tokens"] = write1h,
                            ["cache_read_input_tokens"]        = parseResult.CacheReadInputTokens,
                            ["cache_ttl_1h"]                   = ttl1hUsed
                        }
                    };
                }

                // ── Error / interrupted — decide retry strategy ────────────────────
                if (attempt >= AppConfig.MAX_RETRIES)
                {
                    logger.LogError($"Max retries ({AppConfig.MAX_RETRIES}) reached. Giving up.");
                    return null;
                }

                bool hasPartialText = parseResult?.HasPartialText ?? false;

                if (hasPartialText)
                {
                    int totalChars = parseResult!.BlockTexts.Values.Sum(sb => sb.Length);
                    logger.LogWarning(
                        $"[Retry {attempt}/{AppConfig.MAX_RETRIES}] stream cut at ~{totalChars} chars, continuing");

                    partialTexts = parseResult.GetPartialTexts();

                    JsonArray partialBlocks = SseStreamParser.ReconstructBlocks(parseResult);
                    var contMessages = messages.DeepClone().AsArray();
                    contMessages.Add(new JsonObject
                    {
                        ["role"]    = "assistant",
                        ["content"] = partialBlocks
                    });

                    var pendingToolUseIds = partialBlocks
                        .Where(b => b?["type"]?.GetValue<string>() == "tool_use")
                        .Select(b => b!["id"]?.GetValue<string>() ?? "")
                        .Where(id => !string.IsNullOrEmpty(id))
                        .ToList();

                    foreach (string abandonedId in pendingToolUseIds)
                        logger.LogToolAbandoned(abandonedId,
                            "connection interrupted mid-call before this tool actually ran; " +
                            "retried automatically.");

                    JsonNode continuationContent;
                    if (pendingToolUseIds.Count > 0)
                    {
                        var userBlocks = new JsonArray();
                        foreach (string toolId in pendingToolUseIds)
                        {
                            userBlocks.Add(new JsonObject
                            {
                                ["type"]        = "tool_result",
                                ["tool_use_id"] = toolId,
                                ["is_error"]    = true,
                                ["content"]     = "This tool call was interrupted by a network error " +
                                                   "before it could run and was NOT executed. Please " +
                                                   "retry it (or continue) now."
                            });
                        }
                        userBlocks.Add(new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = "Your response was interrupted by a network error. " +
                                       "Please continue exactly from where you left off, " +
                                       "without repeating anything you already said."
                        });
                        continuationContent = userBlocks;
                    }
                    else
                    {
                        continuationContent = "Your response was interrupted by a network error. " +
                                              "Please continue exactly from where you left off, " +
                                              "without repeating anything you already said.";
                    }

                    // §3.B: Inlined continuation message — no MessageBuilder dependency.
                    contMessages.Add(new JsonObject
                    {
                        ["role"]    = "user",
                        ["content"] = continuationContent
                    });
                    activeMessages = contMessages;
                }
                else
                {
                    logger.LogWarning(
                        $"[Retry {attempt}/{AppConfig.MAX_RETRIES}] connection error, retrying");
                    activeMessages = messages; // reset to original
                    partialTexts   = null;     // no partial to merge
                }
            }

            return null;
        }

        // Keep-alive is switched off for the run after 3 API rejections IN A ROW (a success resets the counter).
        private static volatile bool _keepAliveRejected;
        private static int           _keepAliveRejectCount;

        /// <summary>True after 3 rejected pings in a row: no more pings this run.</summary>
        public static bool KeepAliveDisabled => _keepAliveRejected;

        /// <summary>Reason of the last failed ping (null when the last ping was fine). Shown in the GUI.</summary>
        public static volatile string? LastKeepAliveError;

        /// <summary>
        /// CACHE KEEP-ALIVE ping. Used while a long tool (cmake, tests, CLI) runs, so the 5-minute prompt cache of the
        /// conversation does not expire. <paramref name="messages"/> is the live history and still ENDS with the
        /// assistant tool_use message that has no tool_result yet. We drop that message, so the request is the same
        /// prefix the last real call used (same tools, system, thinking settings => same cache key).
        /// A pin breakpoint is put on the last user message. The first ping therefore writes that one message
        /// (the real next call would have written it anyway). Every later ping is a pure cache READ.
        /// max_tokens is tiny, so the output cost is nearly zero. The answer is thrown away.
        /// Returns the usage of the ping, or null when nothing was sent or it failed. Never throws for API errors.
        /// </summary>
        public static async Task<TokenUsage?> KeepCacheWarmAsync(
            JsonArray         messages,
            JsonArray         tools,
            string            systemPrompt,
            string            thinking,
            string            effort,
            IAgentLogger      logger,
            CancellationToken ct = default)
        {
            if (_keepAliveRejected) return null;
            LastKeepAliveError = null;   // so an old error is never shown for a ping that was not even sent
            if (messages.Count < 2) return null;
            if (messages[messages.Count - 1]?["role"]?.GetValue<string>() != "assistant") return null;

            // Snapshot (deep clone) without the unanswered assistant message. Never touch the live array.
            var snapshot = new JsonArray();
            for (int i = 0; i < messages.Count - 1; i++)
                snapshot.Add(messages[i]?.DeepClone());

            if (snapshot[snapshot.Count - 1] is not JsonObject lastUser ||
                lastUser["role"]?.GetValue<string>() != "user")
                return null;

            // The API allows MAX 4 cache_control blocks per request. BuildPayload already adds: system (1) + last tool (1)
            // + one per message that carries the pin marker (a Sub Agent history has one: the shared parent prefix)
            // + the rolling one (1). Only add MY pin on the last message when there is still room. Without this check
            // a Sub Agent ping had 5 blocks and the API answered HTTP 400.
            int pins = 0;
            foreach (JsonNode? m in snapshot)
            {
                if (m is JsonObject mo && mo[CachePinKey] is JsonValue pv && pv.TryGetValue(out bool isPinned) && isPinned)
                    pins++;
            }
            int toolBlocks = 0;
            foreach (JsonNode? t in tools)
            {
                if (t is JsonObject to && !to.ContainsKey("type")) { toolBlocks = 1; break; }
            }
            int breakpointsWithoutMine = 1 + toolBlocks + pins + 1;
            if (breakpointsWithoutMine < 4)
                lastUser[CachePinKey] = true;   // BuildPayload turns this into a real breakpoint and removes the key
            // else: no room. The ping is then the same request as a normal call. The last message is billed as plain input.

            bool ttl1h = AppConfig.CACHE_TTL_1H && !_ttl1hRejected;
            JsonObject pingPayload = BuildPayload(
                snapshot, tools, systemPrompt, 16, thinking, effort, !_eagerStreamingRejected, ttl1h);

            try
            {
                string url = AppConfig.AZURE_BASE.TrimEnd('/') + "/v1/messages";

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(90));

                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Options.Set(LoggerOptionKey, logger);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AppConfig.AZURE_KEY);
                request.Headers.Add("anthropic-version", "2023-06-01");
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                request.Content = new TimedJsonContent(pingPayload, MeasurePayload(pingPayload), null);

                var sw = System.Diagnostics.Stopwatch.StartNew();
                using HttpResponseMessage response = await _sharedProxyClient.Value.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

                if (!response.IsSuccessStatusCode)
                {
                    string errBody = await response.Content.ReadAsStringAsync(cts.Token);
                    int code = (int)response.StatusCode;
                    // One bad ping (for example a conversation with too many breakpoints) must not kill keep-alive for
                    // the whole run. Only 3 rejections in a row switch it off.
                    if (code >= 400 && code < 500 && code != 408 && code != 429)
                        _keepAliveRejectCount++;
                    bool off = _keepAliveRejectCount >= 3;
                    if (off) _keepAliveRejected = true;
                    LastKeepAliveError = $"HTTP {code}: {TryExtractError(errBody)}";
                    logger.LogWarning($"[cache keep-alive] ping failed: HTTP {code}: {TryExtractError(errBody)}" +
                                      (off ? " — keep-alive is switched OFF for the rest of this run (3 rejections in a row)." : ""));
                    return null;
                }

                _keepAliveRejectCount = 0;
                LastKeepAliveError    = null;
                string sse = await response.Content.ReadAsStringAsync(cts.Token);

                int inTok = 0, outTok = 0, write = 0, write1h = 0, read = 0;
                foreach (string rawLine in sse.Split('\n'))
                {
                    string line = rawLine.Trim();
                    if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

                    JsonNode? ev;
                    try { ev = JsonNode.Parse(line.Substring(5).Trim()); }
                    catch { continue; }

                    string? type = ev?["type"]?.GetValue<string>();
                    if (type == "message_start")
                    {
                        JsonNode? u = ev?["message"]?["usage"];
                        inTok   = u?["input_tokens"]?.GetValue<int>()                ?? 0;
                        write   = u?["cache_creation_input_tokens"]?.GetValue<int>() ?? 0;
                        read    = u?["cache_read_input_tokens"]?.GetValue<int>()     ?? 0;
                        write1h = u?["cache_creation"]?["ephemeral_1h_input_tokens"]?.GetValue<int>() ?? 0;
                    }
                    else if (type == "message_delta")
                    {
                        outTok = ev?["usage"]?["output_tokens"]?.GetValue<int>() ?? outTok;
                    }
                }

                write1h = Math.Clamp(write1h, 0, write);
                logger.LogInfo(
                    $"[cache keep-alive] ping ok in {sw.Elapsed.TotalSeconds:F1}s | read={read:N0} write={write:N0} " +
                    $"uncached-in={inTok:N0} out={outTok:N0}" +
                    (read == 0 ? " | WARNING: nothing was read from the cache — it was probably already expired" : ""));

                return new TokenUsage
                {
                    InputTokens                = inTok,
                    OutputTokens               = outTok,
                    CacheCreationInputTokens   = write,
                    CacheCreation1hInputTokens = write1h,
                    CacheReadInputTokens       = read,
                    CacheTtl1h                 = ttl1h && write1h > 0,
                };
            }
            catch (OperationCanceledException)
            {
                return null;   // tool finished (or the user cancelled) while the ping was in flight
            }
            catch (Exception ex)
            {
                LastKeepAliveError = ex.Message;
                logger.LogWarning($"[cache keep-alive] ping failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>Assemble the full JSON request body for one API call.</summary>
        private static JsonObject BuildPayload(
            JsonArray activeMessages,
            JsonArray tools,
            string    systemPrompt,
            int       maxTokens,
            string    thinking,
            string    effort,
            bool      eagerToolStreaming = false,
            bool      ttl1h              = false)
        {
            // ── Clone so we never mutate the caller's live arrays ─────────────────
            JsonArray clonedMessages = activeMessages.DeepClone().AsArray();

            StripKeyRecursive(clonedMessages, "thought_signature");
            var clonedTools = new JsonArray();
            foreach (JsonNode? _tool in tools)
            {
                if (_tool is JsonObject _obj && !_obj.ContainsKey("type"))
                {
                    JsonNode clone = _tool.DeepClone();

                    if (eagerToolStreaming &&
                        clone is JsonObject cloneObj &&
                        cloneObj["name"] is JsonNode nameNode &&
                        nameNode.GetValueKind() == JsonValueKind.String &&
                        EagerStreamingTools.Contains(nameNode.GetValue<string>()))
                    {
                        cloneObj["eager_input_streaming"] = true;
                    }

                    clonedTools.Add(clone);
                }
            }

            // TTL rule of the API: a 1-hour breakpoint must not come AFTER a 5-minute one (order: tools, system, messages).
            // So when ttl1h is on, EVERY breakpoint below is 1h; when off, every breakpoint is the 5-minute default.
            var systemBlock = new JsonArray
            {
                new JsonObject
                {
                    ["type"]          = "text",
                    ["text"]          = systemPrompt,
                    ["cache_control"] = MakeCacheControl(ttl1h)
                }
            };

            if (clonedTools.Count > 0)
                clonedTools[clonedTools.Count - 1]!.AsObject()["cache_control"] = MakeCacheControl(ttl1h);

            // ── Pinned breakpoint (see CachePinKey) ───────────────────────────────
            // A message may carry the marker CachePinKey. We turn it into a real cache breakpoint
            // and ALWAYS remove the marker, so it never reaches the API.
            // Total breakpoints stay <= 4: system + tools + pin + rolling.
            foreach (JsonNode? m in clonedMessages)
            {
                if (m is not JsonObject mo || !mo.ContainsKey(CachePinKey)) continue;
                bool pinned = mo[CachePinKey] is JsonValue pv && pv.TryGetValue(out bool b) && b;
                mo.Remove(CachePinKey);
                if (pinned) AddCacheBreakpoint(mo, ttl1h);
            }

            if (clonedMessages.Count > 0)
            {
                int cacheIdx = clonedMessages.Count >= 2
                    ? clonedMessages.Count - 2
                    : 0;
                AddCacheBreakpoint(clonedMessages[cacheIdx]!.AsObject(), ttl1h);
            }

            // ── Build the payload ─────────────────────────────────────────────────
            var payload = new JsonObject
            {
                ["model"]      = AppConfig.MODEL,
                ["max_tokens"] = maxTokens,
                ["system"]     = systemBlock,
                ["messages"]   = clonedMessages,
                ["tools"]      = clonedTools,
                ["stream"]     = true
            };

            switch (thinking)
            {
                case "adaptive":
                    payload["thinking"]      = new JsonObject
                    {
                        ["type"]    = "adaptive",
                        ["display"] = "summarized"
                    };
                    payload["output_config"] = new JsonObject { ["effort"] = effort };
                    break;

                case "disabled":
                    payload["thinking"] = new JsonObject { ["type"] = "disabled" };
                    break;
            }

            return payload;
        }

        /// <summary>Attach an Anthropic prompt-cache breakpoint to the last content block of <paramref name="message"/>.</summary>
        private static void AddCacheBreakpoint(JsonObject message, bool ttl1h)
        {
            JsonNode? content = message["content"];
            JsonObject cc = MakeCacheControl(ttl1h);

            // String shorthand (e.g. simple user turn) → convert to block array.
            if (content is JsonValue strVal && strVal.TryGetValue<string>(out string? text))
            {
                message["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"]          = "text",
                        ["text"]          = text,
                        ["cache_control"] = cc
                    }
                };
                return;
            }

            // Block array → stamp the last block.
            if (content is JsonArray arr && arr.Count > 0)
                arr[arr.Count - 1]!.AsObject()["cache_control"] = cc;
        }

        /// <summary>Recursively remove every key named <paramref name="key"/> from a JSON tree (object/array), in place.</summary>
        private static void StripKeyRecursive(JsonNode? node, string key)
        {
            if (node is JsonObject obj)
            {
                try
                {
                    obj.Remove(key);
                    foreach (var kv in obj.ToList())
                        StripKeyRecursive(kv.Value, key);
                }
                catch (ArgumentException)
                {
                }
            }
            else if (node is JsonArray arr)
            {
                foreach (JsonNode? child in arr)
                    StripKeyRecursive(child, key);
            }
        }

        // Only in verbose mode: an EventListener on System.Net.* makes the runtime build an event object for EVERY
        // network event (allocations, and it keeps the event sources busy) — pure waste when nothing is logged.
        private static readonly HttpDiagnosticsEventListener? _netDiagListener =
            SessionFileLogger.IsVerbose ? new HttpDiagnosticsEventListener() : null;
        /// <summary>Shared, long-lived HttpClient + handler reused across EVERY call and EVERY retry for the lifetime of the process.</summary>
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
                ConnectCallback = async (context, ct) =>
                {
                    context.InitialRequestMessage.Options.TryGetValue(LoggerOptionKey, out IAgentLogger? logger);
                    return await RaceConnectAsync(context.DnsEndPoint, logger, ct);
                }
            };
            return new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
        });

        private static readonly HttpRequestOptionsKey<IAgentLogger> LoggerOptionKey = new("AgentLogger");
        private static async Task<Stream> RaceConnectAsync(
            DnsEndPoint       endpoint,
            IAgentLogger?     logger,
            CancellationToken ct)
        {
            var raceClock = System.Diagnostics.Stopwatch.StartNew();
            using var loserCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            var inFlight = new List<Task<Socket>>();
            int launched = 0;

            Task<Socket> LaunchAttempt()
            {
                int n = ++launched;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                logger?.LogInfo($"[ConnectRace] --> launching attempt #{n} to {endpoint} (t={raceClock.Elapsed.TotalSeconds:F1}s, {inFlight.Count + 1} now in flight)");
                return ConnectOnceAsync(endpoint, n, sw, raceClock, logger, loserCts.Token);
            }

            try
            {
                inFlight.Add(LaunchAttempt());

                while (true)
                {
                    var oneSecondTick = Task.Delay(TimeSpan.FromSeconds(1), ct);
                    Task finished = await Task.WhenAny(inFlight.Cast<Task>().Append(oneSecondTick));

                    if (ReferenceEquals(finished, oneSecondTick))
                    {
                        logger?.LogInfo($"[ConnectRace] 1.0s elapsed at t={raceClock.Elapsed.TotalSeconds:F1}s with no winner yet — piling on another attempt");
                        inFlight.Add(LaunchAttempt());
                        continue;
                    }

                    var completed = (Task<Socket>)finished;
                    inFlight.Remove(completed);

                    if (completed.Status == TaskStatus.RanToCompletion)
                    {
                        var winnerSocket = completed.Result;
                        logger?.LogInfo($"[ConnectRace] *** WINNER *** attempt won at t={raceClock.Elapsed.TotalSeconds:F1}s — cancelling {inFlight.Count} loser(s)");
                        loserCts.Cancel();
                        ObserveAndDiscard(inFlight);
                        return new NetworkStream(winnerSocket, ownsSocket: true);
                    }

                    _ = completed.Exception;
                    if (inFlight.Count == 0)
                    {
                        await Task.Delay(200, ct);
                        inFlight.Add(LaunchAttempt());
                    }
                }
            }
            finally
            {
                loserCts.Cancel(); // safety net — make sure nothing is left running
                ObserveAndDiscard(inFlight);
            }
        }

        /// <summary>One raw TCP connect attempt for <see cref="RaceConnectAsync"/>.</summary>
        private static async Task<Socket> ConnectOnceAsync(
            DnsEndPoint                   endpoint,
            int                           attemptNumber,
            System.Diagnostics.Stopwatch  sw,
            System.Diagnostics.Stopwatch  raceClock,
            IAgentLogger?                 logger,
            CancellationToken             ct)
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(endpoint, ct);
                logger?.LogInfo($"[ConnectRace]     attempt #{attemptNumber} CONNECTED in {sw.Elapsed.TotalMilliseconds:F0}ms (t={raceClock.Elapsed.TotalSeconds:F1}s)");
                return socket;
            }
            catch (OperationCanceledException)
            {
                // Lost the race (someone else won) or caller cancelled — expected, quiet.
                socket.Dispose();
                throw;
            }
            catch (Exception ex)
            {
                logger?.LogInfo($"[ConnectRace]     attempt #{attemptNumber} FAILED after {sw.Elapsed.TotalMilliseconds:F0}ms: {ex.Message}");
                socket.Dispose();
                throw;
            }
        }

        private static void ObserveAndDiscard(IEnumerable<Task> tasks)
        {
            foreach (var t in tasks)
                _ = t.ContinueWith(x => { _ = x.Exception; }, TaskContinuationOptions.ExecuteSynchronously);
        }

        /// <summary>
        /// The request body, as UTF-8 JSON, one message at a time. Yields the bytes written so far after every message;
        /// the caller sends them and clears <paramref name="buf"/>. Same JSON as serializing the whole payload at once,
        /// but the biggest buffer is one message, not the whole history.
        /// </summary>
        private static IEnumerable<ReadOnlyMemory<byte>> PayloadChunks(JsonObject payload, ArrayBufferWriter<byte> buf)
        {
            using var w = new Utf8JsonWriter(buf);
            w.WriteStartObject();
            foreach (KeyValuePair<string, JsonNode?> kv in payload)
            {
                w.WritePropertyName(kv.Key);
                if (kv.Key == "messages" && kv.Value is JsonArray messages)
                {
                    w.WriteStartArray();
                    foreach (JsonNode? m in messages)
                    {
                        if (m == null) w.WriteNullValue();
                        else           m.WriteTo(w);
                        w.Flush();
                        yield return buf.WrittenMemory;
                    }
                    w.WriteEndArray();
                }
                else if (kv.Value == null) w.WriteNullValue();
                else                       kv.Value.WriteTo(w);
            }
            w.WriteEndObject();
            w.Flush();
            yield return buf.WrittenMemory;
        }

        /// <summary>Exact byte length of the payload (a counting pass: nothing is kept).</summary>
        private static long MeasurePayload(JsonObject payload)
        {
            var  buf   = new ArrayBufferWriter<byte>(64 * 1024);
            long total = 0;
            foreach (ReadOnlyMemory<byte> chunk in PayloadChunks(payload, buf))
            {
                total += chunk.Length;
                buf.Clear();
            }
            return total;
        }

        /// <summary>The JSON request body, streamed straight into the connection (see PayloadChunks).</summary>
        private sealed class TimedJsonContent : HttpContent
        {
            private readonly JsonObject   _payload;
            private readonly long         _length;
            private readonly IAgentLogger? _logger;

            public TimedJsonContent(JsonObject payload, long length, IAgentLogger? logger)
            {
                _payload = payload;
                _length  = length;
                _logger  = logger;
                Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            }

            protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
                SerializeToStreamAsync(stream, context, CancellationToken.None);

            protected override bool TryComputeLength(out long length)
            {
                length = _length;
                return true;
            }

            protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                _logger?.LogInfo($"[UploadTiming] Writing request body ({_length / 1024.0:F1} KB) to the wire...");
                try
                {
                    var buf = new ArrayBufferWriter<byte>(64 * 1024);
                    foreach (ReadOnlyMemory<byte> chunk in PayloadChunks(_payload, buf))
                    {
                        await stream.WriteAsync(chunk, cancellationToken);
                        buf.Clear();
                    }
                    _logger?.LogInfo($"[UploadTiming] Body upload finished in {sw.Elapsed.TotalMilliseconds:F0}ms ({_length / 1024.0:F1} KB).");
                }
                catch (Exception ex)
                {
                    _logger?.LogInfo($"[UploadTiming] Body upload FAILED after {sw.Elapsed.TotalMilliseconds:F0}ms: {ex.Message}");
                    throw;
                }
            }
        }

        /// <summary>Extract the human-readable error message from an Anthropic API error body.</summary>
        private static string TryExtractError(string body)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out JsonElement err))
                    if (err.TryGetProperty("message", out JsonElement msg))
                        return msg.GetString() ?? body;
            }
            catch { }
            return body;
        }
    }
}
