using System;
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

        // Set once if the API answers 400 because of eager_input_streaming.
        private static volatile bool _eagerStreamingRejected;
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

                try
                {
                    bool eagerSent = !_eagerStreamingRejected;
                    string jsonBody = BuildPayload(activeMessages, tools, systemPrompt, maxTokens, thinking, effort, eagerSent)
                                          .ToJsonString();

                    
                    HttpClient client = _sharedProxyClient.Value; // shared/reused — do NOT dispose

                    long payloadBytes = Encoding.UTF8.GetByteCount(jsonBody);
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
                            request.Content = new TimedStringContent(jsonBody, Encoding.UTF8, "application/json", logger);

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
                        if (eagerSent && response.StatusCode == HttpStatusCode.BadRequest)
                        {
                            string badBody = await response.Content.ReadAsStringAsync();
                            if (badBody.Contains("eager_input_streaming", StringComparison.OrdinalIgnoreCase) ||
                                badBody.Contains("Extra inputs are not permitted", StringComparison.OrdinalIgnoreCase))
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

                // ── Success ───────────────────────────────────────────────────────
                if (parseResult != null && !parseResult.WasInterrupted)
                {
                    JsonArray contentBlocks = SseStreamParser.ReconstructBlocks(parseResult);

                    // If this was a continuation retry, merge the partial text that
                    // was already printed before the error with the new continuation.
                    if (partialTexts != null)
                        SseStreamParser.MergePartialTexts(contentBlocks, partialTexts);

                    return new JsonObject
                    {
                        ["stop_reason"] = parseResult.StopReason,
                        ["content"]     = contentBlocks,
                        // Embed token usage so AgenticLoop can accumulate and log it.
                        ["usage"]       = new JsonObject
                        {
                            ["input_tokens"]                = parseResult.InputTokens,
                            ["output_tokens"]               = parseResult.OutputTokens,
                            ["cache_creation_input_tokens"] = parseResult.CacheCreationInputTokens,
                            ["cache_read_input_tokens"]     = parseResult.CacheReadInputTokens
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

        /// <summary>Assemble the full JSON request body for one API call.</summary>
        private static JsonObject BuildPayload(
            JsonArray activeMessages,
            JsonArray tools,
            string    systemPrompt,
            int       maxTokens,
            string    thinking,
            string    effort,
            bool      eagerToolStreaming = false)
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

            var systemBlock = new JsonArray
            {
                new JsonObject
                {
                    ["type"]          = "text",
                    ["text"]          = systemPrompt,
                    ["cache_control"] = new JsonObject { ["type"] = "ephemeral" }
                }
            };

            if (clonedTools.Count > 0)
                clonedTools[clonedTools.Count - 1]!.AsObject()["cache_control"] =
                    new JsonObject { ["type"] = "ephemeral" };

            if (clonedMessages.Count > 0)
            {
                int cacheIdx = clonedMessages.Count >= 2
                    ? clonedMessages.Count - 2
                    : 0;
                AddCacheBreakpoint(clonedMessages[cacheIdx]!.AsObject());
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
        private static void AddCacheBreakpoint(JsonObject message)
        {
            JsonNode? content = message["content"];
            var cc = new JsonObject { ["type"] = "ephemeral" };

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

        private static readonly HttpDiagnosticsEventListener _netDiagListener = new();
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

        private sealed class TimedStringContent : StringContent
        {
            private readonly IAgentLogger? _logger;

            public TimedStringContent(string content, Encoding encoding, string mediaType, IAgentLogger? logger)
                : base(content, encoding, mediaType)
            {
                _logger = logger;
            }

            protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                long bytes = Headers.ContentLength ?? -1;
                _logger?.LogInfo($"[UploadTiming] Writing request body ({bytes / 1024.0:F1} KB) to the wire...");
                try
                {
                    await base.SerializeToStreamAsync(stream, context, cancellationToken);
                    _logger?.LogInfo($"[UploadTiming] Body upload finished in {sw.Elapsed.TotalMilliseconds:F0}ms ({bytes / 1024.0:F1} KB).");
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
