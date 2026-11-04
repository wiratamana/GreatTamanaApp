using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace GTA.Core
{
    internal static class SseStreamParser
    {

        /// <summary>Parse one SSE response stream.</summary>
        public static async Task<SseParseResult> ParseAsync(
            Stream            stream,
            bool              debug,
            IStreamRenderer   renderer,
            CancellationToken ct         = default,
            Action?           onActivity = null)
        {
            var result = new SseParseResult();

            bool sawTerminalSignal = false;

            try
            {
                using var reader = new StreamReader(stream, Encoding.UTF8);

                string? line;
                while ((line = await reader.ReadLineAsync(ct)) != null)
                {
                    try { onActivity?.Invoke(); } catch { /* best-effort only */ }
                    if (!line.StartsWith("data:")) continue;

                    string dataStr = line["data:".Length..].Trim();
                    if (dataStr == "[DONE]") { sawTerminalSignal = true; break; }

                    JsonNode? node;
                    try   { node = JsonNode.Parse(dataStr); }
                    catch { continue; }
                    if (node == null) continue;

                    string eventType = node["type"]?.GetValue<string>() ?? "";

                    switch (eventType)
                    {
                        case "message_start":
                            HandleMessageStart(node, result);
                            break;

                        case "content_block_start":
                            await HandleBlockStart(node, result, renderer);
                            break;

                        case "content_block_delta":
                            HandleBlockDelta(node, result, renderer);
                            break;

                        case "content_block_stop":
                            HandleBlockStop(node, result, renderer);
                            break;

                        case "message_delta":
                            result.StopReason = node["delta"]?["stop_reason"]?.GetValue<string>()
                                                ?? result.StopReason;
                            if (node["delta"]?["stop_reason"] != null)
                                sawTerminalSignal = true;
                            // Final output token count arrives here (overrides the
                            // placeholder "1" that message_start puts in usage.output_tokens).
                            if (node["usage"]?["output_tokens"] is JsonNode outTok)
                                result.OutputTokens = outTok.GetValue<int>();
                            break;

                        case "message_stop":
                            sawTerminalSignal = true;
                            break;

                        // Known but intentionally ignored: ping
                        default:
                            if (debug && !string.IsNullOrEmpty(eventType))
                                renderer.OnLogLine(
                                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [UNHANDLED EVENT] " +
                                    $"type=\"{eventType}\"  raw={dataStr}");
                            break;
                    }
                }

                if (!sawTerminalSignal)
                {
                    if (debug)
                        renderer.OnLogLine(
                            "\n  [ERROR] Stream ended (clean EOF) without a terminal event " +
                            "(message_delta.stop_reason / message_stop / [DONE]) — treating " +
                            "as an interrupted/truncated response, not a completed one.");
                    result.WasInterrupted = true;
                }
            }
            catch (Exception ex)
            {
                if (debug)
                {
                    renderer.OnLogLine($"\n  [ERROR] {ex.Message}");
                    if (ex.InnerException != null)
                        renderer.OnLogLine($"  [ERROR] Inner: {ex.InnerException.Message}");
                }
                result.WasInterrupted = true;
            }

            return result;
        }

        /// <summary>Parse the message_start event to capture input and cache token counts.</summary>
        private static void HandleMessageStart(JsonNode node, SseParseResult r)
        {
            JsonNode? usage = node["message"]?["usage"];
            if (usage == null) return;

            if (usage["input_tokens"]                is JsonNode inp) r.InputTokens              = inp.GetValue<int>();
            if (usage["cache_creation_input_tokens"] is JsonNode cre) r.CacheCreationInputTokens = cre.GetValue<int>();
            if (usage["cache_read_input_tokens"]     is JsonNode hit) r.CacheReadInputTokens     = hit.GetValue<int>();
        }

        private static async Task HandleBlockStart(
            JsonNode node, SseParseResult r, IStreamRenderer renderer)
        {
            int    idx       = node["index"]!.GetValue<int>();
            string blockType = node["content_block"]?["type"]?.GetValue<string>() ?? "";
            r.BlockTypes[idx] = blockType;

            switch (blockType)
            {
                case "text":
                    r.BlockTexts[idx] = new StringBuilder();
                    await renderer.OnBlockStart(idx, "text", "", "");
                    break;

                case "thinking":
                    r.BlockThinkings[idx] = new StringBuilder();
                    await renderer.OnBlockStart(idx, "thinking", "", "");
                    break;

                case "tool_use":
                    string toolId   = node["content_block"]?["id"]?.GetValue<string>()   ?? "";
                    string toolName = node["content_block"]?["name"]?.GetValue<string>() ?? "";
                    r.BlockToolIds[idx]    = toolId;
                    r.BlockToolNames[idx]  = toolName;
                    r.BlockToolInputs[idx] = new StringBuilder();
                    await renderer.OnBlockStart(idx, "tool_use", toolId, toolName);
                    break;
            }
        }

        private static void HandleBlockDelta(
            JsonNode node, SseParseResult r, IStreamRenderer renderer)
        {
            int    idx       = node["index"]!.GetValue<int>();
            string deltaType = node["delta"]?["type"]?.GetValue<string>() ?? "";

            switch (deltaType)
            {
                case "text_delta":
                {
                    string chunk = node["delta"]?["text"]?.GetValue<string>() ?? "";
                    if (r.BlockTexts.TryGetValue(idx, out var sb)) sb.Append(chunk);
                    renderer.OnTextChunk(idx, chunk);
                    break;
                }

                case "thinking_delta":
                {
                    string chunk = node["delta"]?["thinking"]?.GetValue<string>() ?? "";
                    if (r.BlockThinkings.TryGetValue(idx, out var sb)) sb.Append(chunk);
                    renderer.OnThinkingChunk(idx, chunk);
                    break;
                }

                case "signature_delta":
                {
                    string sig = node["delta"]?["signature"]?.GetValue<string>() ?? "";
                    if (!string.IsNullOrEmpty(sig))
                        r.BlockSignatures[idx] = sig;
                    // Signature is metadata only — no renderer call needed.
                    break;
                }

                case "input_json_delta":
                {
                    string chunk    = node["delta"]?["partial_json"]?.GetValue<string>() ?? "";
                    string toolName = r.BlockToolNames.TryGetValue(idx, out string? tn) ? tn : "";
                    if (r.BlockToolInputs.TryGetValue(idx, out var sb)) sb.Append(chunk);
                    renderer.OnToolInputChunk(idx, toolName, chunk);
                    break;
                }
            }
        }

        private static void HandleBlockStop(
            JsonNode node, SseParseResult r, IStreamRenderer renderer)
        {
            int idx = node["index"]!.GetValue<int>();
            if (!r.BlockTypes.TryGetValue(idx, out string? bt)) return;

            // Compute the accumulated character count for this block.
            int charCount = bt switch
            {
                "text"     => r.BlockTexts.TryGetValue(idx, out var tSb)  ? tSb.Length  : 0,
                "thinking" => r.BlockThinkings.TryGetValue(idx, out var thSb) ? thSb.Length : 0,
                "tool_use" => r.BlockToolInputs.TryGetValue(idx, out var tiSb) ? tiSb.Length : 0,
                _          => 0
            };

            renderer.OnBlockStop(idx, bt, charCount);
        }

        public static JsonArray ReconstructBlocks(SseParseResult r)
        {
            var result  = new JsonArray();
            var indexes = r.BlockTypes.Keys.OrderBy(k => k).ToList();

            foreach (int idx in indexes)
            {
                string bt = r.BlockTypes[idx];
                switch (bt)
                {
                    case "text":
                        result.Add(new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = r.BlockTexts.TryGetValue(idx, out var tSb) ? tSb.ToString() : ""
                        });
                        break;

                    case "thinking":
                        result.Add(new JsonObject
                        {
                            ["type"]      = "thinking",
                            ["thinking"]  = r.BlockThinkings.TryGetValue(idx, out var thSb) ? thSb.ToString() : "",
                            ["signature"] = r.BlockSignatures.TryGetValue(idx, out string? sig) ? sig : ""
                        });
                        break;

                    case "tool_use":
                        string inputJson = r.BlockToolInputs.TryGetValue(idx, out var inSb) ? inSb.ToString() : "{}";
                        JsonNode? inputNode = ParseToolInputSafely(inputJson);

                        result.Add(new JsonObject
                        {
                            ["type"]  = "tool_use",
                            ["id"]    = r.BlockToolIds.TryGetValue(idx, out string? tid) ? tid : "",
                            ["name"]  = r.BlockToolNames.TryGetValue(idx, out string? tn) ? tn : "",
                            ["input"] = inputNode ?? new JsonObject()
                        });
                        break;
                }
            }

            return result;
        }

        private static JsonNode? ParseToolInputSafely(string json)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    return JsonNode.Parse(json);

                var obj = new JsonObject();
                foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
                    obj[prop.Name] = JsonNode.Parse(prop.Value.GetRawText());

                return obj;
            }
            catch
            {
                return new JsonObject();
            }
        }

        public static void MergePartialTexts(JsonArray contentBlocks, Dictionary<int, string> partialTexts)
        {
            string allPartial = string.Concat(
                partialTexts.OrderBy(kv => kv.Key).Select(kv => kv.Value));

            if (string.IsNullOrEmpty(allPartial)) return;

            // Find the first text block in the continuation response and prepend.
            foreach (JsonNode? block in contentBlocks)
            {
                if (block?["type"]?.GetValue<string>() == "text")
                {
                    string existing = block["text"]?.GetValue<string>() ?? "";
                    block["text"]   = allPartial + existing;
                    return;
                }
            }

            // No text block in continuation — add one at the front.
            contentBlocks.Insert(0, new JsonObject
            {
                ["type"] = "text",
                ["text"] = allPartial
            });
        }
    }
}
