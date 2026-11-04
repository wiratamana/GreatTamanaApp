using System;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace GTA.Core
{
    internal sealed class GeminiStreamResult
    {
        public JsonArray ContentBlocks = new();
        public string    StopReason    = "end_turn";

        public int InputTokens      = 0;
        public int CandidatesTokens = 0;
        public int ThoughtsTokens   = 0;
        public int CachedTokens     = 0;

        /// <summary>True when the stream was cut (network exception, or ended without a terminal finishReason/[DONE]) before the model genuinely finished.</summary>
        public bool WasInterrupted = false;

        /// <summary>True when at least one finalized "text" block has non-empty content.</summary>
        public bool HasPartialText
        {
            get
            {
                foreach (JsonNode? b in ContentBlocks)
                    if (b?["type"]?.GetValue<string>() == "text" &&
                        !string.IsNullOrEmpty(b["text"]?.GetValue<string>()))
                        return true;
                return false;
            }
        }
    }

    internal static class GeminiStreamParser
    {
        public static async Task<GeminiStreamResult> ParseAsync(
            Stream            stream,
            bool              debug,
            IStreamRenderer   renderer,
            CancellationToken ct         = default,
            Action?           onActivity = null)
        {
            var result = new GeminiStreamResult();

            bool   sawTerminalSignal = false;
            bool   hasFuncCall       = false;
            string finishReasonSeen  = "STOP";
            int    blockIndex        = 0;

            // ── Currently-open text/thinking block state (flushed on kind change,
            //    on a functionCall, or at end-of-stream/interruption) ────────────
            string?       openKind = null; // "text" | "thinking"
            StringBuilder openText = new();
            string?       openSignature = null;

            async Task FlushOpenBlockAsync()
            {
                if (openKind == null) return;

                renderer.OnBlockStop(blockIndex, openKind, openText.Length);

                JsonObject block = openKind == "thinking"
                    ? new JsonObject { ["type"] = "thinking", ["thinking"] = openText.ToString(), ["signature"] = "" }
                    : new JsonObject { ["type"] = "text",     ["text"]     = openText.ToString() };
                if (!string.IsNullOrEmpty(openSignature))
                    block["thought_signature"] = openSignature;

                result.ContentBlocks.Add(block);
                blockIndex++;

                openKind = null;
                openText = new StringBuilder();
                openSignature = null;
                await Task.CompletedTask;
            }

            try
            {
                using var reader = new StreamReader(stream, Encoding.UTF8);

                string? line;
                while ((line = await reader.ReadLineAsync(ct)) != null)
                {
                    try { onActivity?.Invoke(); } catch { /* best-effort only */ }
                    if (!line.StartsWith("data:")) continue;

                    string dataStr = line["data:".Length..].Trim();
                    if (dataStr.Length == 0) continue;
                    if (dataStr == "[DONE]") { sawTerminalSignal = true; break; }

                    JsonNode? node;
                    try   { node = JsonNode.Parse(dataStr); }
                    catch { continue; }
                    if (node == null) continue;

                    if (node["error"] != null)
                    {
                        if (debug) renderer.OnLogLine($"[Gemini ERROR] {node["error"]?["message"]?.GetValue<string>()}");
                        break; // treated as interrupted below unless a terminal signal already arrived
                    }

                    JsonNode? candidate = node["candidates"]?[0];
                    if (candidate != null)
                    {
                        string? finishReasonRaw = candidate["finishReason"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(finishReasonRaw))
                        {
                            finishReasonSeen  = finishReasonRaw;
                            sawTerminalSignal = true;
                        }

                        if (candidate["content"]?["parts"] is JsonArray partsArr)
                        {
                            foreach (JsonNode? part in partsArr)
                            {
                                if (part == null) continue;

                                if (part["functionCall"] is JsonNode fc)
                                {
                                    await FlushOpenBlockAsync();

                                    hasFuncCall = true;
                                    string    name       = fc["name"]?.GetValue<string>() ?? "";
                                    JsonNode? args       = fc["args"]?.DeepClone() ?? new JsonObject();
                                    string    toolId     = $"call_{blockIndex}__{name}";
                                    string    argsJson   = args.ToJsonString();
                                    string?   thoughtSig = part["thoughtSignature"]?.GetValue<string>();

                                    await renderer.OnBlockStart(blockIndex, "tool_use", toolId, name);
                                    renderer.OnToolInputChunk(blockIndex, name, argsJson);
                                    renderer.OnBlockStop(blockIndex, "tool_use", argsJson.Length);

                                    var toolBlock = new JsonObject
                                    {
                                        ["type"]  = "tool_use",
                                        ["id"]    = toolId,
                                        ["name"]  = name,
                                        ["input"] = args,
                                    };
                                    if (!string.IsNullOrEmpty(thoughtSig))
                                        toolBlock["thought_signature"] = thoughtSig;

                                    result.ContentBlocks.Add(toolBlock);
                                    blockIndex++;
                                }
                                else if (part["text"] != null)
                                {
                                    string  chunk      = part["text"]?.GetValue<string>() ?? "";
                                    bool    isThought  = part["thought"]?.GetValue<bool>() ?? false;
                                    string  kind       = isThought ? "thinking" : "text";
                                    string? thoughtSig = part["thoughtSignature"]?.GetValue<string>();

                                    if (openKind != null && openKind != kind)
                                        await FlushOpenBlockAsync();

                                    if (openKind == null)
                                    {
                                        openKind = kind;
                                        await renderer.OnBlockStart(blockIndex, kind, "", "");
                                    }

                                    openText.Append(chunk);
                                    if (!string.IsNullOrEmpty(thoughtSig))
                                        openSignature = thoughtSig;

                                    if (isThought) renderer.OnThinkingChunk(blockIndex, chunk);
                                    else           renderer.OnTextChunk(blockIndex, chunk);
                                }
                                // Other part kinds (executable code, etc.) skipped gracefully.
                            }
                        }
                    }

                    if (node["usageMetadata"] is JsonNode usage)
                    {
                        if (usage["promptTokenCount"]      is JsonNode inp)  result.InputTokens      = inp.GetValue<int>();
                        if (usage["candidatesTokenCount"]  is JsonNode cand) result.CandidatesTokens = cand.GetValue<int>();
                        if (usage["thoughtsTokenCount"]    is JsonNode th)   result.ThoughtsTokens   = th.GetValue<int>();
                        if (usage["cachedContentTokenCount"] is JsonNode cc) result.CachedTokens     = cc.GetValue<int>();
                    }
                }

                await FlushOpenBlockAsync(); // finalize whatever's still open (clean finish OR interruption)

                if (!sawTerminalSignal)
                {
                    if (debug)
                        renderer.OnLogLine(
                            "\n  [ERROR] Gemini stream ended without a terminal signal " +
                            "(finishReason / [DONE]) — treating as an interrupted/truncated response.");
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
                await FlushOpenBlockAsync();
                result.WasInterrupted = true;
            }

            result.StopReason = hasFuncCall
                ? "tool_use"
                : finishReasonSeen switch
                {
                    "MAX_TOKENS" => "max_tokens",
                    _            => "end_turn",
                };

            return result;
        }
    }
}
