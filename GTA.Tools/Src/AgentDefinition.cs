namespace GTA.Tools
{
    /// <summary>Describes one named agent: its system prompt and the set of tool tags that determines which tools it may use.</summary>
    public sealed class AgentDefinition
    {
        /// <summary>Unique agent name used as the key in <see cref="AgentRegistry"/> and passed to the <c>call_agent</c> tool.</summary>
        public string Name { get; init; } = "";

        /// <summary>System prompt injected verbatim when the agent's LLM session starts.</summary>
        public string SystemPrompt { get; init; } = "";

        public string[] ToolTags { get; init; } = System.Array.Empty<string>();

        /// <summary>Maximum tokens for the API call. Defaults to 128 000.</summary>
        public int MaxTokens { get; init; } = 128_000;

        /// <summary>Thinking mode: <c>"adaptive"</c> (on by default) | <c>"disabled"</c> (sends explicit disabled flag).</summary>
        public string Thinking { get; init; } = "adaptive";

        /// <summary>Effort level for adaptive thinking: <c>"low"</c> | <c>"medium"</c> | <c>"high"</c> | <c>"xhigh"</c> | <c>"max"</c>.</summary>
        public string Effort { get; init; } = "high";
    }
}
