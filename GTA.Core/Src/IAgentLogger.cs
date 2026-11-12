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

        /// <summary>
        /// Called by the cache keep-alive after EACH ping it sent while a long tool runs, so the host can show it in the
        /// GUI. <paramref name="ok"/> false = the ping failed (<paramref name="error"/> says why, may be null).
        /// <paramref name="usage"/> is the usage of the ping (cache read / write / uncached input). Default: no-op.
        /// </summary>
        void LogCacheKeepAlive(int pingNumber, bool ok, TokenUsage usage, string? error) { }

        /// <summary>
        /// Called right before AgenticLoop starts running a dispatched sub agent's own isolated nested loop.
        /// Several of these can be active AT THE SAME TIME (parallel sub agents), so an implementation must keep
        /// its state per returned logger, not in one single field. Must be safe to call from any thread.
        /// </summary>
        (IAgentLogger logger, IStreamRenderer renderer) BeginSubAgentDispatch(
            string task, string modelName, IStreamRenderer mainRenderer) => (this, mainRenderer);

        /// <summary>
        /// Like <see cref="BeginSubAgentDispatch"/>, but for a Sub Agent that was paused and is now RESUMED: the host
        /// should re-attach to the existing twin session <paramref name="twinSessionId"/> instead of creating a new one.
        /// Default: starts a normal new dispatch.
        /// </summary>
        (IAgentLogger logger, IStreamRenderer renderer) ResumeSubAgentDispatch(
            string twinSessionId, string task, string modelName, IStreamRenderer mainRenderer)
            => BeginSubAgentDispatch(task, modelName, mainRenderer);

        /// <summary>The id of the host session this logger writes to (null if the host has none). Used to remember a Sub Agent's twin.</summary>
        string? SessionId => null;

        /// <summary>
        /// Closes ONE dispatch. <paramref name="subLogger"/> is the logger that BeginSubAgentDispatch returned for it.
        /// Must be safe to call from any thread, also while other dispatches are still running.
        /// </summary>
        void EndSubAgentDispatch(IAgentLogger subLogger, bool success, string message) { }

        /// <summary>
        /// The host's pause gate for the session this logger writes to (or null if the host has none).
        /// A dispatched sub agent checks this gate before every round, so pausing the sub agent's own
        /// session really holds it.
        /// </summary>
        IPromptInjectionQueue? PauseGate => null;
    }
}
