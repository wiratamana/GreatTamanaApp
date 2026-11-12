using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using GTA.Core;

namespace GTA.App;

/// <summary>A single assistant thinking block captured during an LLM turn.</summary>
public class DisplayThinkingBlock
{
    public string         Text          { get; set; } = string.Empty;
    public DateTimeOffset Timestamp     { get; set; } = DateTimeOffset.Now;

    /// <summary>Monotonically-increasing position of this event within the current turn.</summary>
    public int            SequenceIndex { get; set; } = 0;
}

/// <summary>A single tool-call record captured during an LLM turn.</summary>
public class DisplayToolCall
{
    public string         ToolName      { get; set; } = string.Empty;
    public string         InputJson     { get; set; } = "{}";
    public string         Result        { get; set; } = string.Empty;
    public DateTimeOffset StartedAt     { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset CompletedAt   { get; set; } = DateTimeOffset.Now;

    /// <summary>Monotonically-increasing position of this event within the current turn.</summary>
    public int            SequenceIndex { get; set; } = 0;
}

/// <summary>A single prose text segment emitted by the assistant during a turn.</summary>
public class DisplayTextSegment
{
    public string         Text          { get; set; } = string.Empty;
    public DateTimeOffset Timestamp     { get; set; } = DateTimeOffset.Now;

    /// <summary>Monotonically-increasing position of this text section within the turn.</summary>
    public int            SequenceIndex { get; set; } = 0;
}

public class DisplayJiraInjection
{
    public string TicketKey { get; set; } = string.Empty;
    public string Content   { get; set; } = string.Empty;
}

public class DisplayPromptInjection
{
    public string         Text            { get; set; } = string.Empty;
    public List<string>   Files           { get; set; } = new();
    public List<string>   ToolNamesToLoad { get; set; } = new();
    public DateTimeOffset Timestamp       { get; set; } = DateTimeOffset.Now;

    /// <summary>Monotonically-increasing position of this event within the current turn — assigned when LlmChatWindow.AddInjectionWidget records it.</summary>
    public int             SequenceIndex  { get; set; } = 0;
}
/// <summary>
/// The widget "⏸ Currently paused" inside the turn's bubble (looks like the cache keep-alive note). It is written when the
/// turn is saved at a pause. After ▶ Resume the SAME widget turns green ("Resumed") and the turn goes on in the same bubble.
/// Saved with the chat, so it is also there after an app restart. It is never sent to the LLM.
/// </summary>
public class DisplayPauseMarker
{
    /// <summary>Position in the turn's timeline (same counter as thinking / tool calls / text).</summary>
    public int            SequenceIndex { get; set; } = 0;

    public DateTimeOffset PausedAt      { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? ResumedAt    { get; set; } = null;

    /// <summary>"" = still paused. "resumed" = continued. "stopped" = the paused work was stopped / thrown away.</summary>
    public string         Outcome       { get; set; } = string.Empty;
}


public class DisplaySubAgentDispatch
{
    public string TwinSessionId { get; set; } = string.Empty;

    /// <summary>The exact task text the sub agent was dispatched with.</summary>
    public string Task    { get; set; } = string.Empty;

    /// <summary>The sub agent's own final 'status' — true for 'success'.</summary>
    public bool   Success { get; set; } = false;

    /// <summary>The sub agent's own final 'message'.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Monotonically-increasing position of this event within the current turn — assigned when LlmChatWindow.AddSubAgentDispatchWidget records it.</summary>
    public int    SequenceIndex { get; set; } = 0;
}

/// <summary>
/// FRONT-END ONLY record of one agentic round: its number, where it starts in the turn's timeline and its token
/// usage / cost. Saved to session.json so the round number + hover details survive an app restart. It is never
/// sent to the LLM and never read by the agent loop.
/// </summary>
public class DisplayRound
{
    /// <summary>The loop's own round number (1-based, restarts for every user prompt).</summary>
    public int            Round         { get; set; } = 0;

    /// <summary>Position in the turn's timeline: everything with a higher SequenceIndex (until the next round) belongs to this round.</summary>
    public int            SequenceIndex { get; set; } = 0;

    public DateTimeOffset Timestamp     { get; set; } = DateTimeOffset.Now;

    /// <summary>Token usage of THIS round only (null until the model call of the round has finished).</summary>
    public TokenUsage?    Usage         { get; set; } = null;

    public double?        CostUsd       { get; set; } = null;

    /// <summary>Seconds spent waiting on the model call of this round (null when unknown).</summary>
    public double?        ElapsedSeconds { get; set; } = null;

    // ── Cache keep-alive (pings sent while this round's tools ran a long time; shown as a note + in the hover popup) ──

    /// <summary>Number of keep-alive pings that worked in this round.</summary>
    public int            KeepAlivePings        { get; set; } = 0;

    /// <summary>Number of keep-alive pings that failed in this round.</summary>
    public int            KeepAliveFailures     { get; set; } = 0;

    /// <summary>Tokens the pings read from the prompt cache (a big number = the cache was really kept alive).</summary>
    public int            KeepAliveReadTokens   { get; set; } = 0;

    /// <summary>USD cost of all pings of this round (not included in <see cref="CostUsd"/>, but in the session total).</summary>
    public double         KeepAliveCostUsd      { get; set; } = 0;

    /// <summary>Reason of the last failed ping of this round (null when none failed).</summary>
    public string?        KeepAliveLastError    { get; set; } = null;
}

/// <summary>Kind of a single unit inside a Task's ordered "prompt blueprint" — see TaskUnit / TaskUnits / LlmChatWindow.Task.cs / TaskBuilderWindow.</summary>
public enum TaskUnitKind
{
    /// <summary>Send PromptText to the LLM and fully await its response.</summary>
    Prompt,

    /// <summary>Pause for WaitSeconds before continuing to the next unit.</summary>
    Wait,

    ChainTask,
}

/// <summary>One step inside a Task's ordered "prompt blueprint" (see TaskUnits).</summary>
public class TaskUnit
{
    public string       Id          { get; set; } = Guid.NewGuid().ToString();
    public TaskUnitKind Kind        { get; set; } = TaskUnitKind.Prompt;

    /// <summary>The prompt text sent to the LLM when this unit fires.</summary>
    public string       PromptText  { get; set; } = string.Empty;

    /// <summary>How long (in seconds) to pause before continuing to the next unit.</summary>
    public double       WaitSeconds { get; set; } = 60;

    /// <summary>Absolute target date/time (LOCAL machine time) a Wait unit pauses until.</summary>
    public DateTime?    WaitUntil   { get; set; } = null;

    public string WaitSummary
    {
        get
        {
            if (WaitUntil.HasValue)
                return $"Wait until {WaitUntil.Value:yyyy-MM-dd HH:mm}";

            if (WaitSeconds >= 3600) return $"Wait {WaitSeconds / 3600.0:0.##}h";
            if (WaitSeconds >= 60)   return $"Wait {WaitSeconds / 60.0:0.##}m";
            return $"Wait {WaitSeconds:0.##}s";
        }
    }

    /// <summary>Absolute file paths (images or text files) attached to THIS step's own prompt — only meaningful for Kind == Prompt.</summary>
    public List<string> Attachments { get; set; } = new();

    /// <summary>Tool names pre-loaded ONLY for THIS step's own prompt — only meaningful for Kind == Prompt.</summary>
    public List<string> PreloadedToolNames { get; set; } = new();

    public string? ChildSessionId { get; set; } = null;

    /// <summary>0 for a normal top-level step (shown as "Task N" everywhere — N derived from this unit's plain array position).</summary>
    public int SubTaskNumber { get; set; } = 0;

    public string? ChainTaskSessionId { get; set; } = null;

    public string? ChainTaskSessionTitle { get; set; } = null;

    public int RestartLabelIndex { get; set; } = -1;

    public bool IsSupersededByRestart { get; set; } = false;
}

/// <summary>One UI bubble in the chat transcript.</summary>
public class ChatMessage
{
    /// <summary>"user" or "assistant"</summary>
    public string         Role           { get; set; } = "user";
    public string         Content        { get; set; } = string.Empty;
    public DateTimeOffset Timestamp      { get; set; } = DateTimeOffset.Now;

    /// <summary>Absolute file paths attached to a user message (empty for assistant).</summary>
    public List<string>               AttachedImages  { get; set; } = new();

    /// <summary>Thinking blocks streamed by the assistant during this turn.</summary>
    public List<DisplayThinkingBlock>  ThinkingBlocks  { get; set; } = new();

    /// <summary>Tool calls executed during this assistant turn, in chronological order.</summary>
    public List<DisplayToolCall>       ToolCalls       { get; set; } = new();

    /// <summary>Individual prose text segments emitted by the assistant, in chronological order (each carries its own SequenceIndex).</summary>
    public List<DisplayTextSegment>    TextSegments    { get; set; } = new();

    public List<DisplayJiraInjection>  JiraInjections  { get; set; } = new();

    public List<DisplayPromptInjection> PromptInjections { get; set; } = new();


    /// <summary>⏸ Pause widgets of this turn (see DisplayPauseMarker). A resumed turn continues in the SAME bubble.</summary>
    public List<DisplayPauseMarker> PauseMarkers { get; set; } = new();
    public List<DisplaySubAgentDispatch> SubAgentDispatches { get; set; } = new();

    /// <summary>Front-end-only round markers (number + per-round tokens/cost) — see DisplayRound.</summary>
    public List<DisplayRound> Rounds { get; set; } = new();

    /// <summary>Cumulative token usage for this whole assistant turn (all agentic rounds).</summary>
    public TokenUsage? Usage     { get; set; } = null;

    /// <summary>Exact model name that produced this turn (e.g. "claude-sonnet-5-5", "gpt-6.1-sol").</summary>
    public string?     ModelName { get; set; } = null;

    public double?      CostUsd   { get; set; } = null;
}

/// <summary>A named conversation session.</summary>
public class ChatSession
{
    public string             Id                  { get; init; }  = Guid.NewGuid().ToString();
    public string             Title               { get; set; }   = "New Chat";
    public DateTimeOffset     CreatedAt           { get; init; }  = DateTimeOffset.Now;
    public DateTimeOffset     LastModifiedAt      { get; set; }   = DateTimeOffset.Now;

    /// <summary>Short preview shown in the sidebar (≤ 80 chars of first user message).</summary>
    public string             Preview             { get; set; }   = string.Empty;

    /// <summary>Total display message count (used by sidebar before full load).</summary>
    public int                MessageCount        { get; set; }   = 0;

    /// <summary>Non-null while this session is a SCHEDULED prompt still waiting to fire.</summary>
    public DateTimeOffset?    ScheduledAt         { get; set; }   = null;

    /// <summary>The exact prompt text that will be sent automatically once ScheduledAt is reached (or immediately via "Start Now").</summary>
    public string?            ScheduledPrompt     { get; set; }   = null;

    public List<string>       ScheduledAttachments { get; set; } = new();

    public DateTimeOffset?    TaskScheduledAt { get; set; } = null;

    public bool                IsTask   { get; set; } = false;

    /// <summary>The ordered "prompt blueprint" assembled in TaskBuilderWindow — see TaskUnit.</summary>
    public List<TaskUnit>      TaskUnits { get; set; } = new();

    public bool                TaskHasRun { get; set; } = false;

    // ── Task model/backend caching (see LlmChatWindow.Task.cs — RunTaskAsync) ──

    public string? TaskBackendName { get; set; } = null;

    public string?             ParentTaskId { get; set; } = null;

    /// <summary>
    /// Non-null when this session was started by ANOTHER session — today that is a dispatched Sub Agent
    /// (see LlmChatWindow.SubAgentDispatch.cs). The sidebar shows such a session nested under the session that
    /// dispatched it (Task → Task step → Sub Agent …) and the link is saved to metadata.json, so the nesting
    /// survives an app restart. It is independent of ParentTaskId, which only links a Task step to its Task.
    /// </summary>
    public string?             ParentSessionId { get; set; } = null;

    /// <summary>True while the user has explicitly paused THIS session via the header "⏸ Pause" button.</summary>
    public bool IsPaused { get; set; } = false;

    /// <summary>
    /// True from the moment a prompt is sent into this session until its turn finishes. Persisted: a session
    /// loaded with this still true was killed or crashed mid-turn, so the sidebar shows INTERRUPTED, not DONE.
    /// </summary>
    public bool TurnInProgress { get; set; } = false;

    /// <summary>true after LoadSessionData has populated Messages and ConversationHistory.</summary>
    public bool               IsFullyLoaded       { get; set; }   = false;

    /// <summary>true when this session has genuinely changed in memory (a new user/assistant message was actually added) since it was last saved.</summary>
    public bool               IsDirty             { get; set; }   = false;

    /// <summary>UI display messages (one per user turn + one per assistant turn).</summary>
    public List<ChatMessage>  Messages            { get; }        = new();

    /// <summary>Persistent raw API message array for the LLM context window.</summary>
    public JsonArray          ConversationHistory { get; }        = new();

    public ChatSessionRuntime Runtime { get; } = new();
}
