using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace GTA.Core
{
    internal sealed class SseParseResult
    {
        public Dictionary<int, string>        BlockTypes      = new();
        public Dictionary<int, StringBuilder> BlockTexts      = new();
        public Dictionary<int, StringBuilder> BlockThinkings  = new();
        public Dictionary<int, string>        BlockSignatures = new();
        public Dictionary<int, string>        BlockToolIds    = new();
        public Dictionary<int, string>        BlockToolNames  = new();
        public Dictionary<int, StringBuilder> BlockToolInputs = new();
        public string StopReason     = "end_turn";

        /// <summary>Regular input tokens (from message_start.message.usage.input_tokens).</summary>
        public int InputTokens               = 0;

        /// <summary>Output tokens generated (from message_delta.usage.output_tokens).</summary>
        public int OutputTokens              = 0;

        /// <summary>Cache-write tokens (from message_start.message.usage.cache_creation_input_tokens).</summary>
        public int CacheCreationInputTokens  = 0;

        /// <summary>Cache-hit tokens (from message_start.message.usage.cache_read_input_tokens).</summary>
        public int CacheReadInputTokens      = 0;

        /// <summary>1-hour part of the cache-write tokens (message_start usage.cache_creation.ephemeral_1h_input_tokens).</summary>
        public int CacheCreation1hInputTokens = 0;

        /// <summary>True when the API returned the usage.cache_creation 5m/1h breakdown object at all.</summary>
        public bool CacheCreationBreakdownSeen = false;

        /// <summary>Snapshot of all accumulated token counts as a <see cref="TokenUsage"/> value.</summary>
        public TokenUsage Usage => new()
        {
            InputTokens                = InputTokens,
            OutputTokens               = OutputTokens,
            CacheCreationInputTokens   = CacheCreationInputTokens,
            CacheCreation1hInputTokens = CacheCreation1hInputTokens,
            CacheReadInputTokens       = CacheReadInputTokens,
        };

        // ── Stream-state flags ────────────────────────────────────────────────

        /// <summary>True when the stream was cut by a network exception before the final message_stop event.</summary>
        public bool WasInterrupted = false;

        /// <summary>True when at least one text block has partial content.</summary>
        public bool HasPartialText => BlockTexts.Values.Any(sb => sb.Length > 0);

        /// <summary>Snapshot of all text block content as plain strings.</summary>
        public Dictionary<int, string> GetPartialTexts() =>
            BlockTexts.ToDictionary(kv => kv.Key, kv => kv.Value.ToString());
    }
}
