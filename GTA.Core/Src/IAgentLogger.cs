namespace GTA.Core
{
    /// <summary>Thin logging interface for the agentic loop and HTTP transport layer.</summary>
    public interface IAgentLogger
    {
        void LogInfo      (string message);
        void LogWarning   (string message);
        void LogError     (string message);

        /// <summary>Called at the start of each agentic round (including round 1).</summary>
        void LogRound     (int round);

        /// <summary>Called when a tool is about to be executed.</summary>
        void LogToolCall  (string toolName, string inputPreview);
        void LogToolCall(string toolName, string toolId, string inputPreview) => LogToolCall(toolName, inputPreview);

        /// <summary>
        /// Called right BEFORE a tool executes, with the COMPLETE input JSON (the LogToolCall overloads only get a
        /// 200-char preview). Hosts use it for write-ahead logging. Default: no-op.
        /// </summary>
        void LogToolCallFull(string toolName, string toolId, string fullInputJson) { }

        /// <summary>Called with the result after a tool execution.</summary>
        void LogToolResult(string resultPreview);

        void LogToolResult(string toolId, string resultPreview) => LogToolResult(resultPreview);

        void LogToolAbandoned(string toolId, string reason) { }

        /// <summary>Called when the agentic loop ends (stop_reason banner).</summary>
        void LogStopReason(string reason);

        /// <summary>Called after each API response with the token usage for that call.</summary>
        void LogTokenUsage(TokenUsage usage, bool isFinal = false);

        void OnConnectionWait(bool connecting, System.Action? cancelCurrentAttempt = null) { }

        void LogInjection(PendingInjection injection) { }

        /// <summary>Live progress feedback emitted WHILE a single tool call is actually executing</summary>
        void LogToolProgress(string toolName, string message) { }

        bool LastCallExceededContextLength { get => false; set { } }

        /// <summary>Called right before AgenticLoop starts running a dispatched sub agent's own isolated nested loop.</summary>
        (IAgentLogger logger, IStreamRenderer renderer) BeginSubAgentDispatch(
            string task, string modelName, IStreamRenderer mainRenderer) => (this, mainRenderer);

        void EndSubAgentDispatch(bool success, string message) { }

        /// <summary>
        /// The host's pause gate for the session this logger writes to (or null if the host has none).
        /// A dispatched sub agent checks this gate before every round, so pausing the sub agent's own
        /// session really holds it.
        /// </summary>
        IPromptInjectionQueue? PauseGate => null;
    }
}
