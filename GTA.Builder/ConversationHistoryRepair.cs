using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using GTA.Core;

namespace GTA.Builder
{
    /// <summary>
    /// Keeps the model history (the JSON array that is sent to the API) VALID, also after the user pressed STOP.
    ///
    /// The API has hard rules. Break one and EVERY later call of that chat fails with HTTP 400:
    ///   1. Every tool_use block of an assistant message needs a tool_result with the same id in the NEXT message.
    ///   2. Every tool_result needs a tool_use with the same id in the message right before it.
    ///   3. No message may have empty content.
    ///   4. User/assistant should alternate (two user messages in a row are merged into one).
    ///
    /// STOP can break rule 1 at any moment (the assistant message with its tool_use is already in the history,
    /// the tool results are not yet). <see cref="Repair"/> fixes all four rules. It runs
    ///   - at the start of every turn (this also heals chats that were already broken or saved mid-turn), and
    ///   - when a turn is stopped (<see cref="CloseInterruptedTurn"/>).
    /// It never removes real content: it only adds the missing tool_results (with a clear "stopped" text),
    /// drops orphans / empty messages and merges neighbours of the same role.
    /// </summary>
    internal static class ConversationHistoryRepair
    {
        public const string StoppedToolResultText =
            "⏹ This tool call was STOPPED by the user (STOP button) before it finished or before it could run. " +
            "There is NO real result. Do not assume it succeeded. If it changed something (file, process, " +
            "repository), check the current state before you repeat it.";

        public const string StopNoteMarker = "⏹ [User pressed STOP]";

        private const string StopNoteText =
            StopNoteMarker + " The previous turn was interrupted by the user. Anything you were doing is " +
            "unfinished. Read the next user message and follow it. Do not resume the interrupted work " +
            "unless the user asks for it.";

        /// <summary>Fixes the history in place. Returns how many problems were fixed (0 = it was already valid).</summary>
        public static int Repair(JsonArray messages, IAgentLogger? logger = null)
        {
            int fixes = 0;

            fixes += DropBrokenAndEmpty(messages);
            fixes += MergeSameRole(messages);
            fixes += AddMissingToolResults(messages);
            fixes += RemoveOrphanToolResults(messages);
            fixes += DropBrokenAndEmpty(messages);   // orphan removal can leave an empty message behind
            fixes += MergeSameRole(messages);

            if (fixes > 0)
            {
                string msg = $"History repair: fixed {fixes} problem(s) " +
                             "(unanswered tool call, orphan tool result, empty or neighbouring same-role message).";
                SessionFileLogger.Log("HISTORY_REPAIR", msg);
                logger?.LogInfo(msg);
            }
            return fixes;
        }

        /// <summary>
        /// Called when a turn was cancelled. Makes the history valid and ends it with a short note for the model,
        /// so the next user message (merged into the last user message) is read in the right context.
        /// </summary>
        public static void CloseInterruptedTurn(JsonArray messages, IAgentLogger? logger = null)
        {
            try
            {
                Repair(messages, logger);

                if (messages.Count == 0) return;
                if (messages[^1] is not JsonObject last) return;
                if (RoleOf(last) != "user") return;   // e.g. the turn ended with an assistant message: nothing to add

                JsonArray blocks = EnsureBlockArray(last);
                if (EndsWithStopNote(blocks)) return;

                blocks.Add(new JsonObject { ["type"] = "text", ["text"] = StopNoteText });
            }
            catch (Exception ex)
            {
                // Never let the repair itself turn a clean STOP into a crash.
                SessionFileLogger.LogException("CloseInterruptedTurn failed", ex);
            }
        }

        /// <summary>A tool_result for a tool call that did not (fully) run because of STOP.</summary>
        public static JsonObject MakeStoppedToolResult(string toolUseId) =>
            MessageBuilder.MakeTextToolResult(toolUseId, StoppedToolResultText);

        // ── Rule 3 ────────────────────────────────────────────────────────────────

        private static int DropBrokenAndEmpty(JsonArray messages)
        {
            int fixes = 0;
            for (int i = messages.Count - 1; i >= 0; i--)
            {
                if (messages[i] is not JsonObject m || RoleOf(m) is not ("user" or "assistant"))
                {
                    messages.RemoveAt(i);
                    fixes++;
                    continue;
                }

                JsonNode? content = m["content"];
                bool empty = content == null
                          || (content is JsonArray arr && arr.Count == 0)
                          || (content is JsonValue v && v.TryGetValue(out string? s) && string.IsNullOrWhiteSpace(s));
                if (empty)
                {
                    messages.RemoveAt(i);
                    fixes++;
                }
            }
            return fixes;
        }

        // ── Rule 4 ────────────────────────────────────────────────────────────────

        private static int MergeSameRole(JsonArray messages)
        {
            int fixes = 0;
            for (int i = messages.Count - 1; i >= 1; i--)
            {
                if (messages[i] is not JsonObject cur || messages[i - 1] is not JsonObject prev) continue;
                if (RoleOf(cur) != RoleOf(prev)) continue;

                JsonArray target = EnsureBlockArray(prev);
                JsonArray source = EnsureBlockArray(cur);

                // Move the blocks over (detach each node from its old parent first).
                while (source.Count > 0)
                {
                    JsonNode? node = source[0];
                    source.RemoveAt(0);
                    target.Add(node);
                }

                // tool_result blocks must stay in front of any text in a user message.
                if (RoleOf(prev) == "user") MoveToolResultsToFront(target);

                messages.RemoveAt(i);
                fixes++;
            }
            return fixes;
        }

        // ── Rule 1 ────────────────────────────────────────────────────────────────

        private static int AddMissingToolResults(JsonArray messages)
        {
            int fixes = 0;
            for (int i = 0; i < messages.Count; i++)
            {
                if (messages[i] is not JsonObject asst || RoleOf(asst) != "assistant") continue;
                if (asst["content"] is not JsonArray blocks) continue;

                List<string> ids = ToolUseIds(blocks);
                if (ids.Count == 0) continue;

                JsonObject? next = i + 1 < messages.Count ? messages[i + 1] as JsonObject : null;
                if (next == null || RoleOf(next) != "user")
                {
                    next = new JsonObject { ["role"] = "user", ["content"] = new JsonArray() };
                    messages.Insert(i + 1, next);
                }

                JsonArray nextBlocks = EnsureBlockArray(next);
                var answered = new HashSet<string>(StringComparer.Ordinal);
                foreach (JsonNode? b in nextBlocks)
                {
                    if (b?["type"]?.GetValue<string>() == "tool_result" &&
                        b["tool_use_id"]?.GetValue<string>() is string rid)
                        answered.Add(rid);
                }

                int insertAt = 0;
                foreach (string id in ids)
                {
                    if (answered.Contains(id)) continue;
                    nextBlocks.Insert(insertAt++, MakeStoppedToolResult(id));
                    fixes++;
                }

                MoveToolResultsToFront(nextBlocks);
            }
            return fixes;
        }

        // ── Rule 2 ────────────────────────────────────────────────────────────────

        private static int RemoveOrphanToolResults(JsonArray messages)
        {
            int fixes = 0;
            for (int i = 0; i < messages.Count; i++)
            {
                if (messages[i] is not JsonObject user || RoleOf(user) != "user") continue;
                if (user["content"] is not JsonArray blocks) continue;

                var valid = new HashSet<string>(StringComparer.Ordinal);
                if (i > 0 && messages[i - 1] is JsonObject prev && RoleOf(prev) == "assistant" &&
                    prev["content"] is JsonArray prevBlocks)
                {
                    foreach (string id in ToolUseIds(prevBlocks)) valid.Add(id);
                }

                for (int b = blocks.Count - 1; b >= 0; b--)
                {
                    if (blocks[b]?["type"]?.GetValue<string>() != "tool_result") continue;
                    string? rid = blocks[b]!["tool_use_id"]?.GetValue<string>();
                    if (rid != null && valid.Contains(rid)) continue;

                    blocks.RemoveAt(b);
                    fixes++;
                }
            }
            return fixes;
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        private static string? RoleOf(JsonObject m) => m["role"]?.GetValue<string>();

        private static List<string> ToolUseIds(JsonArray blocks)
        {
            var ids = new List<string>();
            foreach (JsonNode? b in blocks)
            {
                if (b?["type"]?.GetValue<string>() != "tool_use") continue;
                string? id = b["id"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(id)) ids.Add(id!);
            }
            return ids;
        }

        /// <summary>Makes message["content"] a block array (a plain string becomes one text block) and returns it.</summary>
        private static JsonArray EnsureBlockArray(JsonObject message)
        {
            if (message["content"] is JsonArray arr) return arr;

            var created = new JsonArray();
            if (message["content"] is JsonValue v && v.TryGetValue(out string? text) && !string.IsNullOrEmpty(text))
                created.Add(new JsonObject { ["type"] = "text", ["text"] = text });
            message["content"] = created;
            return created;
        }

        /// <summary>Stable move: all tool_result blocks first, everything else after them in the old order.</summary>
        private static void MoveToolResultsToFront(JsonArray blocks)
        {
            var results = new List<JsonNode?>();
            var others  = new List<JsonNode?>();
            foreach (JsonNode? b in blocks)
                (b?["type"]?.GetValue<string>() == "tool_result" ? results : others).Add(b);

            bool alreadyOrdered = true;
            for (int i = 0; i < results.Count; i++)
                if (!ReferenceEquals(blocks[i], results[i])) { alreadyOrdered = false; break; }
            if (alreadyOrdered) return;

            blocks.Clear();
            foreach (JsonNode? b in results) blocks.Add(b);
            foreach (JsonNode? b in others)  blocks.Add(b);
        }

        private static bool EndsWithStopNote(JsonArray blocks)
        {
            if (blocks.Count == 0) return false;
            string? text = blocks[^1]?["text"]?.GetValue<string>();
            return text != null && text.StartsWith(StopNoteMarker, StringComparison.Ordinal);
        }
    }
}
