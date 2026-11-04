using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GTA.Core;
using Avalonia.Controls;
using Avalonia.Threading;

namespace GTA.App;

public sealed class AvaloniaStreamRenderer : IStreamRenderer
{
    private readonly LlmChatWindow _window;
    private readonly ChatSession   _session;

    private StackPanel? _turnBubblePanel;

    private StackPanel?            _currentTextPanel;
    private readonly StringBuilder _currentTextSb = new();
    private int                    _textUpdatePending;
    private volatile string        _latestTextSnapshot = string.Empty;  // written BG-thread only, read UI-thread

    private TextBlock?             _thinkingBlock;
    private readonly StringBuilder _thinkingText = new();
    private int                    _thinkingUpdatePending;
    private volatile string        _latestThinkingSnapshot = string.Empty; // written BG-thread only, read UI-thread

    private readonly Dictionary<int, StringBuilder> _toolInputAccum = new();

    private readonly Dictionary<int, FileToolCard> _fileCards = new();
    private int _cardUpdatePending;   // coalesces UI updates, see OnToolInputChunk

    private readonly Dictionary<int, string> _toolNameByBlock = new();

    public AvaloniaStreamRenderer(LlmChatWindow window, ChatSession session)
    {
        _window  = window  ?? throw new ArgumentNullException(nameof(window));
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    /// <summary>Returns the turn bubble's content StackPanel, creating it on first call.</summary>
    private async Task<StackPanel> EnsureTurnBubbleAsync()
    {
        if (_turnBubblePanel == null)
        {
            _turnBubblePanel = await Dispatcher.UIThread
                .InvokeAsync(() => _window.BeginTurnBubble(_session));
        }
        return _turnBubblePanel;
    }

    public async Task OnBlockStart(int blockIndex, string blockType, string toolId, string toolName)
    {
        switch (blockType)
        {
            case "thinking":
            {
                var bubble = await EnsureTurnBubbleAsync();
                _thinkingText.Clear();
                _thinkingBlock = await Dispatcher.UIThread
                    .InvokeAsync(() => _window.AddThinkingSectionToBubble(_session, bubble));
                break;
            }

            case "text":
            {
                // Each text block gets a fresh sub-panel inside the turn bubble.
                var bubble = await EnsureTurnBubbleAsync();
                _currentTextSb.Clear();
                _currentTextPanel = await Dispatcher.UIThread
                    .InvokeAsync(() => _window.AddTextSectionToBubble(_session, bubble));
                break;
            }

            case "tool_use" when !string.IsNullOrEmpty(toolName):
            {
                _toolInputAccum[blockIndex]  = new StringBuilder();
                _toolNameByBlock[blockIndex] = toolName;   // Task 1 fix: store for OnBlockStop
                var bubble = await EnsureTurnBubbleAsync();
                var card = await Dispatcher.UIThread
                    .InvokeAsync(() => _window.BeginToolCallWidget(_session, bubble, toolId, toolName));
                if (card != null)
                    _fileCards[blockIndex] = card;   // live Write/Edit card — fed by OnToolInputChunk
                break;
            }
        }
    }

    public void OnTextChunk(int blockIndex, string chunk)
    {
        _currentTextSb.Append(chunk);
        _latestTextSnapshot = _currentTextSb.ToString(); // BG thread: safe (single writer)

        // Coalesce: only one Render-priority post in-flight at a time.
        if (Interlocked.Exchange(ref _textUpdatePending, 1) == 0)
        {
            Dispatcher.UIThread.Post(() =>
            {
                Interlocked.Exchange(ref _textUpdatePending, 0);

                var panel = _currentTextPanel;
                if (panel != null)
                {
                    // Use the pre-captured snapshot — never touch _currentTextSb here
                    // (BG thread may be appending concurrently).
                    _window.UpdateTextSection(panel, _latestTextSnapshot);
                    _window.ScrollToBottom(_session);
                }
            }, DispatcherPriority.Render);
        }
    }

    public void OnThinkingChunk(int blockIndex, string chunk)
    {
        _thinkingText.Append(chunk);
        _latestThinkingSnapshot = _thinkingText.ToString(); // BG thread: safe (single writer)

        if (Interlocked.Exchange(ref _thinkingUpdatePending, 1) == 0)
        {
            Dispatcher.UIThread.Post(() =>
            {
                Interlocked.Exchange(ref _thinkingUpdatePending, 0);

                var tb = _thinkingBlock;
                if (tb != null)
                    // Use the pre-captured snapshot — never touch _thinkingText here.
                    tb.Text = _latestThinkingSnapshot;
            }, DispatcherPriority.Render);
        }
    }

    public void OnToolInputChunk(int blockIndex, string toolName, string partialJson)
    {
        if (_toolInputAccum.TryGetValue(blockIndex, out StringBuilder? sb))
        {
            lock (sb) sb.Append(partialJson);

            if (_fileCards.TryGetValue(blockIndex, out FileToolCard? liveCard) && liveCard != null &&
                Interlocked.Exchange(ref _cardUpdatePending, 1) == 0)
            {
                FileToolCard card = liveCard;
                _ = Task.Delay(60).ContinueWith(_ =>
                {
                    string snapshot;
                    lock (sb) snapshot = sb.ToString();

                    Dispatcher.UIThread.Post(() =>
                    {
                        Interlocked.Exchange(ref _cardUpdatePending, 0);
                        card.OnInputStreaming(snapshot);
                        _window.ScrollToBottom(_session);
                    }, DispatcherPriority.Render);
                });
            }
        }
    }

    public void OnBlockStop(int blockIndex, string blockType, int charCount)
    {
        switch (blockType)
        {
            case "text":
            {
                string finalText = _currentTextSb.ToString();
                StackPanel? panel = _currentTextPanel;
                _currentTextPanel = null;

                Post(() =>
                {
                    if (panel != null)
                        _window.FinalizeTextSection(_session, panel, finalText);
                    _window.ScrollToBottom(_session);
                });
                break;
            }

            case "thinking":
            {
                string finalThinking = _thinkingText.ToString();
                TextBlock? thinkTb   = _thinkingBlock;
                _thinkingBlock       = null;

                Post(() =>
                {
                    if (thinkTb != null)
                    {
                        thinkTb.Text = finalThinking;
                        _window.FinalizeThinkingSection(_session, thinkTb);
                    }
                });
                // Task 1 fix: persist the thinking block in ChatMessage for session replay.
                _window.RecordThinkingBlock(_session, finalThinking);
                break;
            }

            case "tool_use":
            {
                string inputJson;
                if (_toolInputAccum.TryGetValue(blockIndex, out StringBuilder? sb))
                {
                    lock (sb) inputJson = sb.ToString();
                }
                else
                {
                    inputJson = "{}";
                }
                _toolInputAccum.Remove(blockIndex);

                if (_fileCards.Remove(blockIndex, out FileToolCard? fileCard) && fileCard != null)
                {
                    FileToolCard cardRef = fileCard;
                    string finalJson     = inputJson;
                    // Stream thread, synchronously: the tool runs only after the stream ends,
                    // and the card needs the final input to read the old lines before that.
                    cardRef.SetFinalInput(finalJson);
                    Post(() =>
                    {
                        cardRef.OnInputComplete(finalJson);
                        _window.ScrollToBottom(_session);
                    });
                }

                // Retrieve the tool name stored in OnBlockStart (Task 1 fix).
                string toolNameForRecord = _toolNameByBlock.TryGetValue(blockIndex, out string? tn)
                    ? tn : string.Empty;
                _toolNameByBlock.Remove(blockIndex);

                // Update the parameters panel in the live tool-call widget.
                _window.SetLastToolParameters(_session, inputJson);

                // Task 1 fix: open a tool-call record in ChatMessage for history persistence.
                _window.BeginToolRecord(_session, toolNameForRecord, inputJson);
                break;
            }
        }
    }

    /// <summary>Log lines from the SSE parser (e.g. errors, unhandled events).</summary>
    public void OnLogLine(string message) =>
        Post(() => _window.AddStatusLine(_session, message));

    private static void Post(Action action) =>
        Dispatcher.UIThread.Post(action, DispatcherPriority.Normal);
}
