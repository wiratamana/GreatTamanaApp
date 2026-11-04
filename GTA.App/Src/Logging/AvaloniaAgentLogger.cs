using System;
using GTA.Core;
using GTA.Tools;
using Avalonia.Threading;

namespace GTA.App;

public sealed class AvaloniaAgentLogger : IAgentLogger
{
    private readonly LlmChatWindow _window;
    private readonly ChatSession   _session;

    private string? _lastToolName;

    // True once the whole chat was saved in the current round (reset by LogRound); see LogToolCallFull.
    private bool _checkpointedThisRound;

    public AvaloniaAgentLogger(LlmChatWindow window, ChatSession session)
    {
        _window  = window  ?? throw new ArgumentNullException(nameof(window));
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public void LogInfo   (string message) => Post(message);
    public void LogWarning(string message) => Post($"⚠  {message}");
    public void LogError  (string message) => Post($"✗  {message}");

    /// <summary>Creates a foldable "Round N" section in the chat window.</summary>
    public void LogRound(int round)
    {
        _checkpointedThisRound = false;
        ConversationPersistence.AppendJournal(_session.Id, $"ROUND {round}", "model call starts");

        if (Dispatcher.UIThread.CheckAccess())
            _window.BeginRoundSection(_session, round);
        else
            Dispatcher.UIThread.Post(() => _window.BeginRoundSection(_session, round));
    }

    public void OnConnectionWait(bool connecting, Action? cancelCurrentAttempt = null)
    {
        if (Dispatcher.UIThread.CheckAccess())
            _window.SetConnectingState(_session, connecting, cancelCurrentAttempt);
        else
            Dispatcher.UIThread.Post(() => _window.SetConnectingState(_session, connecting, cancelCurrentAttempt));
    }

    /// <summary>Suppressed (no status line — the renderer's OnBlockStart("tool_use") already creates a foldable tool-call widget that shows the tool name).</summary>
    public void LogToolCall(string toolName, string inputPreview) => _lastToolName = toolName;

    public void LogToolCall(string toolName, string toolId, string inputPreview)
    {
        _lastToolName = toolName;

        // Called right BEFORE the tool executes: the live Edit/Remove card reads the
        // lines that are about to be deleted, so it can show them in red.
        FileToolCard.SnapshotBeforeExecute(toolId);
    }
    /// <summary>
    /// Write-ahead log, called right before the tool executes: the complete tool input goes to the journal and, once
    /// per round before the FIRST tool runs, the WHOLE chat is saved (session.json overwritten).
    /// </summary>
    public void LogToolCallFull(string toolName, string toolId, string fullInputJson)
    {
        const int MaxJournalInput = 20000;
        string input = fullInputJson.Length > MaxJournalInput
            ? fullInputJson[..MaxJournalInput] + $"… (+{fullInputJson.Length - MaxJournalInput} chars)"
            : fullInputJson;
        ConversationPersistence.AppendJournal(_session.Id, $"TOOL CALL {toolName} id={toolId}", input);

        if (!_checkpointedThisRound)
        {
            _checkpointedThisRound = true;
            _window.CheckpointSessionBlocking(_session, $"before tool '{toolName}'");
        }
    }


    public void LogToolProgress(string toolName, string message)
    {
        if (Dispatcher.UIThread.CheckAccess())
            _window.AppendToolProgressLine(_session, message);
        else
            Dispatcher.UIThread.Post(() => _window.AppendToolProgressLine(_session, message));
    }

    /// <summary>Legacy, non-id-aware overload — kept only to satisfy the IAgentLogger interface contract.</summary>
    public void LogToolResult(string fullResult)
    {
        const int MaxPreview = 500;
        string preview = fullResult.Length > MaxPreview
            ? fullResult[..MaxPreview] + $"… (+{fullResult.Length - MaxPreview} chars)"
            : fullResult;
        SessionFileLogger.LogVerbose("TOOL_RESULT", SecretRedactor.Redact(preview));
    }

    /// <summary>Bug 3/4 fix: Set the FULL result on the tool-call widget identified by toolId.</summary>
    public void LogToolResult(string toolId, string fullResult)
    {
        string? toolName = _lastToolName;
        _lastToolName = null;

        string redacted;
        try
        {
            string evidenceDir = ConversationPersistence.EvidenceDir(_session.Id);
            redacted = ImageEvidenceStore.SaveAndRedact(fullResult, evidenceDir, toolName);
        }
        catch (Exception ex)
        {
            SessionFileLogger.LogException($"Evidence save/redact failed for tool '{toolName}'", ex);
            redacted = fullResult; // never let evidence-saving break the chat
        }

        const int MaxPreview = 500;
        string preview = redacted.Length > MaxPreview
            ? redacted[..MaxPreview] + $"… (+{redacted.Length - MaxPreview} chars)"
            : redacted;
        SessionFileLogger.LogVerbose("TOOL_RESULT", SecretRedactor.Redact(preview));
        // Journal: result right after the tool ends (capped; the full result is in session.json).
        const int MaxJournalResult = 4000;
        string journalResult = redacted.Length > MaxJournalResult
            ? redacted[..MaxJournalResult] + $"… (+{redacted.Length - MaxJournalResult} chars, full text in session.json)"
            : redacted;
        ConversationPersistence.AppendJournal(_session.Id, $"TOOL RESULT {toolName ?? "?"} id={toolId}", journalResult);


        _window.SetToolResult(_session, toolId, redacted);      // update live UI widget
        _window.CompleteLastToolRecord(_session, redacted);     // Task 1 fix: persist in history
    }

    public void LogToolAbandoned(string toolId, string reason)
    {
        SessionFileLogger.Log("TOOL_ABANDONED", $"{toolId}: {reason}");
        ConversationPersistence.AppendJournal(_session.Id, "TOOL ABANDONED", $"id={toolId} reason={reason}");

        if (Dispatcher.UIThread.CheckAccess())
            _window.MarkToolAbandoned(_session, toolId, reason);
        else
            Dispatcher.UIThread.Post(() => _window.MarkToolAbandoned(_session, toolId, reason));
    }

    /// <summary>Suppressed: stop-reason details ("end_turn", "tool_use") are internal API details and add noise without helping the user.</summary>
    public void LogStopReason(string reason) { /* suppressed */ }

    public void LogInjection(PendingInjection injection)
    {
        if (Dispatcher.UIThread.CheckAccess())
            _window.AddInjectionWidget(_session, injection);
        else
            Dispatcher.UIThread.Post(() => _window.AddInjectionWidget(_session, injection));
    }

    /// <summary>Display a styled token-usage banner inside the active turn bubble.</summary>
    public void LogTokenUsage(TokenUsage usage, bool isFinal = false)
    {
        // Per-round token lines are verbose-only (noise); the final per-turn total is
        // always logged (and also appended to logs/token_costs.txt).
        if (isFinal)
            SessionFileLogger.Log("TOKENS",
                $"Session total — " +
                $"In={usage.InputTokens:N0}  Out={usage.OutputTokens:N0}  " +
                $"CacheWrite={usage.CacheCreationInputTokens:N0}  CacheRead={usage.CacheReadInputTokens:N0}");
        else
            SessionFileLogger.LogVerbose("TOKENS",
                $"Turn — " +
                $"In={usage.InputTokens:N0}  Out={usage.OutputTokens:N0}  " +
                $"CacheWrite={usage.CacheCreationInputTokens:N0}  CacheRead={usage.CacheReadInputTokens:N0}");
        _window.AddTokenUsageBanner(_session, usage, isFinal);
    }

    public bool LastCallExceededContextLength { get; set; }

    /// <summary>Remembers the twin session (and its own task text) created by BeginSubAgentDispatch so EndSubAgentDispatch knows which one to finalize.</summary>
    private (ChatSession Twin, string Task)? _subAgentDispatch;

    public (IAgentLogger logger, IStreamRenderer renderer) BeginSubAgentDispatch(
        string task, string modelName, IStreamRenderer mainRenderer)
    {
        ChatSession? twin = null;
        using var doneSignal = new System.Threading.ManualResetEventSlim(false);

        void DoWork()
        {
            _session.Runtime.Pause();
            _window.RefreshInjectButtonLabel();

            twin = _window.CreateSubAgentTwinSession(_session, task, modelName);
            doneSignal.Set();
        }

        if (Dispatcher.UIThread.CheckAccess())
            DoWork();
        else
            Dispatcher.UIThread.Post(DoWork);

        doneSignal.Wait();

        _subAgentDispatch = (twin!, task);
        return (new AvaloniaAgentLogger(_window, twin!), new AvaloniaStreamRenderer(_window, twin!));
    }

    public void EndSubAgentDispatch(bool success, string message)
    {
        var dispatch = _subAgentDispatch;
        _subAgentDispatch = null;
        if (dispatch == null) return;

        var (twin, task) = dispatch.Value;

        using var doneSignal = new System.Threading.ManualResetEventSlim(false);

        void DoWork()
        {
            _window.FinishSubAgentTwinSession(twin, success, message);
            _window.AddSubAgentDispatchWidget(_session, twin.Id, task, success, message);

            _session.Runtime.Resume();
            _window.RefreshInjectButtonLabel();

            doneSignal.Set();
        }

        if (Dispatcher.UIThread.CheckAccess())
            DoWork();
        else
            Dispatcher.UIThread.Post(DoWork);

        doneSignal.Wait();
    }

    private void Post(string msg)
    {
        if (Dispatcher.UIThread.CheckAccess())
            _window.AddStatusLine(_session, msg);
        else
            Dispatcher.UIThread.Post(() => _window.AddStatusLine(_session, msg));
    }
}
