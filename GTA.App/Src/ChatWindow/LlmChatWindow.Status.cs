using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GTA.Core;

namespace GTA.App;

public partial class LlmChatWindow
{

    public void AppendLog(string message, bool toFile = true)
    {
        if (toFile) SessionFileLogger.Log(message);

        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() =>
            {
                _logQueue.Enqueue($"[{DateTime.Now:HH:mm:ss}] {message}");
                while (_logQueue.Count > MaxLogLines) _logQueue.Dequeue();
            });
            return;
        }
        _logQueue.Enqueue($"[{DateTime.Now:HH:mm:ss}] {message}");
        while (_logQueue.Count > MaxLogLines) _logQueue.Dequeue();
    }

    public void NotifyScreenshot(string path) =>
        AppendLog($"Screenshot saved → {path}");

    public void AddStatusLine(ChatSession session, string text)
    {
        if (text.StartsWith("⚠") || text.StartsWith("✗"))
            SessionFileLogger.Log("STATUS", text);
        else
            SessionFileLogger.LogVerbose("STATUS", text);
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => AddStatusLineCore(session, text));
            return;
        }
        AddStatusLineCore(session, text);
    }

    private void AddStatusLineCore(ChatSession session, string text)
    {
        var statusBlock = new TextBlock
        {
            Text         = text,
            FontSize     = 11,
            FontFamily   = new FontFamily("Consolas, Courier New, monospace"),
            Foreground   = ColMuted,
            Opacity      = 0.85,
            Margin       = new Thickness(4, 1, 4, 1),
            TextWrapping = TextWrapping.Wrap,
        };

        var runtime = session.Runtime;

        if (runtime.CurrentRoundContentPanel != null)
            runtime.CurrentRoundContentPanel.Children.Add(statusBlock);
        else if (runtime.CurrentTurnContentPanel != null)
            runtime.CurrentTurnContentPanel.Children.Add(statusBlock);
        else
            runtime.MessagesPanel?.Children.Add(statusBlock);

        ScrollToBottom(session);
    }

    public void SetConnectingState(ChatSession session, bool connecting, Action? cancelCurrentAttempt)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => SetConnectingState(session, connecting, cancelCurrentAttempt));
            return;
        }

        var runtime = session.Runtime;
        runtime.CurrentCancelAttempt = connecting ? cancelCurrentAttempt : null;

        if (runtime.CurrentRoundStopwatch != null)
        {
            if (connecting)
                runtime.CurrentRoundStopwatch.Start(); // idempotent — resumes if already running
            else
                runtime.CurrentRoundStopwatch.Stop();
        }

        if (ReferenceEquals(session, _current))
            RefreshChromeForActiveSession();
    }

    private void RefreshChromeForActiveSession()
    {
        var session = _current;
        bool running = (session?.Runtime.IsProcessing ?? false) ||
                       (session?.Runtime.TaskCurrentUnitIndex >= 0);

        bool requested = session?.IsPaused ?? false;
        bool active    = session?.Runtime.IsPauseActive ?? false;
        bool paused    = requested; // "paused" in the broad sense used below

        _inputBox.IsEnabled   = paused || !running;
        _sendButton.IsEnabled = true; // always clickable (cancel/resume support)
        _sendButton.Content   = !requested ? (running ? "⏹" : "➤") : (active ? "▶" : "⏳");
        ToolTip.SetTip(_sendButton, !requested
            ? null
            : (active
                ? "Click to resume this paused session"
                : "Pause requested — still finishing the current round/tool call. Click to " +
                  "cancel the request and resume right away."));

        UiTheme.StyleButton(_sendButton,
            requested ? ColRetryAmber : (running ? ColRunningRed : ColAccent),
            Brushes.White);

        bool canRetryNow = running && session?.Runtime.CurrentCancelAttempt != null;
        _retryButton.IsEnabled = canRetryNow;
        UiTheme.StyleButton(_retryButton,
            canRetryNow ? ColRetryAmber : ColDisabled,
            canRetryNow ? Brushes.White : ColMuted,
            disabledBackground: ColDisabled,
            disabledForeground: ColMuted);

        RefreshPauseButtonLabel();
    }

    public void AddTokenUsageBanner(ChatSession session, TokenUsage usage, bool isFinal)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => AddTokenUsageBanner(session, usage, isFinal));
            return;
        }

        var runtime = session.Runtime;

        runtime.CurrentTurnUsageTotal = isFinal ? usage : runtime.CurrentTurnUsageTotal + usage;

        if (runtime.CurrentTurnMessage != null)
        {
            runtime.CurrentTurnMessage.ModelName = runtime.CurrentTurnModelName;
            runtime.CurrentTurnMessage.Usage     = runtime.CurrentTurnUsageTotal;
            runtime.CurrentTurnMessage.CostUsd   = ModelPricing.TryComputeCost(
                runtime.CurrentTurnModelName, runtime.CurrentTurnUsageTotal, out double totalCostUsd)
                ? totalCostUsd
                : (double?)null;
        }

        double? rowCostUsd = ModelPricing.TryComputeCost(runtime.CurrentTurnModelName, usage, out double c)
            ? c
            : (double?)null;
        if (isFinal)
            AppendSessionTokenCostToFile(session, runtime.CurrentTurnModelName, usage, rowCostUsd);

        var roundRecord = runtime.CurrentRoundRecord;
        if (!isFinal && roundRecord != null)
        {
            // Per-round numbers live in the hover popup of the round number (and are saved with the chat).
            roundRecord.Usage          = usage;
            roundRecord.CostUsd        = rowCostUsd;
            roundRecord.ElapsedSeconds = runtime.CurrentRoundStopwatch?.Elapsed.TotalSeconds;
            if (runtime.CurrentRoundBadge != null)
                ToolTip.SetTip(runtime.CurrentRoundBadge, BuildRoundTooltip(roundRecord));
        }
        else
        {
            var row = BuildTokenUsageRow(usage, isFinal, rowCostUsd);
            if (runtime.CurrentTurnContentPanel != null)
                runtime.CurrentTurnContentPanel.Children.Add(row);
            else
                runtime.MessagesPanel?.Children.Add(row);
        }

        ScrollToBottom(session);
    }

    internal static Control BuildTokenUsageRow(TokenUsage usage, bool isFinal, double? costUsd)
    {
        string label = isFinal ? "Session total" : "Tokens";

        var labelBlock = new TextBlock
        {
            Text              = "📊 " + label,
            FontSize          = 10,
            FontFamily        = new FontFamily("Consolas, Courier New, monospace"),
            Foreground        = ColMuted,
            Opacity           = 0.85,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(0, 0, 3, 0),
        };

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing     = 4,
            Margin      = new Thickness(2, 3, 2, 0),
        };
        row.Children.Add(labelBlock);
        FillTokenChips(row, usage, costUsd);
        return row;
    }

    private static void FillTokenChips(Panel host, TokenUsage usage, double? costUsd)
    {
        host.Children.Add(MakeTokenChip("In",  $"{usage.InputTokens:N0}",  Color.FromRgb(160, 160, 190)));
        host.Children.Add(MakeTokenChip("Out", $"{usage.OutputTokens:N0}", Color.FromRgb(100, 200, 140)));

        if (usage.CacheReadInputTokens > 0)
            host.Children.Add(MakeTokenChip("Read", $"{usage.CacheReadInputTokens:N0}", Color.FromRgb(90, 160, 220)));

        if (usage.CacheCreationInputTokens > 0)
            host.Children.Add(MakeTokenChip("Write", $"{usage.CacheCreationInputTokens:N0}", Color.FromRgb(210, 160, 80)));

        host.Children.Add(MakeTokenChip(
            "$", costUsd.HasValue ? $"{costUsd.Value:0.0000}" : "N/A",
            Color.FromRgb(230, 200, 90)));
    }

    private static Border MakeTokenChip(string key, string value, Color accent)
    {
        var tb = new TextBlock
        {
            FontSize          = 10,
            FontFamily        = new FontFamily("Consolas, Courier New, monospace"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        tb.Inlines!.Add(new Avalonia.Controls.Documents.Run(key + " ")
        {
            Foreground = new SolidColorBrush(Color.FromArgb(150, accent.R, accent.G, accent.B)),
        });
        tb.Inlines.Add(new Avalonia.Controls.Documents.Run(value)
        {
            Foreground = new SolidColorBrush(accent),
            FontWeight = FontWeight.SemiBold,
        });

        return new Border
        {
            Background      = new SolidColorBrush(Color.FromArgb(26, accent.R, accent.G, accent.B)),
            BorderBrush     = new SolidColorBrush(Color.FromArgb(70, accent.R, accent.G, accent.B)),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(8),
            Padding         = new Thickness(6, 0, 6, 0),
            Child           = tb,
        };
    }

    private static readonly object _tokenCostFileLock = new();

    private static void AppendSessionTokenCostToFile(ChatSession session, string? modelName, TokenUsage usage, double? costUsd)
    {
        try
        {
            string logsDir = System.IO.Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(logsDir);
            string filePath = System.IO.Path.Combine(logsDir, "token_costs.txt");

            string costText = costUsd.HasValue ? $"${costUsd.Value:0.0000}" : "N/A";
            string modelText = string.IsNullOrWhiteSpace(modelName) ? "(unknown)" : modelName;
            string line =
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Session=\"{session.Title}\" (id={session.Id}) " +
                $"Model={modelText} " +
                $"In={usage.InputTokens:N0} Out={usage.OutputTokens:N0} " +
                $"Write$={usage.CacheCreationInputTokens:N0} Cache✓={usage.CacheReadInputTokens:N0} " +
                $"Cost={costText}";

            lock (_tokenCostFileLock)
            {
                File.AppendAllText(filePath, line + Environment.NewLine, System.Text.Encoding.UTF8);
            }
        }
        catch
        {
        }
    }

    private void BeginSessionTurn(ChatSession session)
    {
        session.Runtime.IsProcessing = true;

        if (ReferenceEquals(session, _current))
            RefreshChromeForActiveSession();

        ShowProcessingBadge(session);
    }

    private void FinishSessionTurn(ChatSession session)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => FinishSessionTurn(session));
            return;
        }

        session.Runtime.ClearAfterTurnFinished();

        // The turn is over (success, stop or error): clear the "killed mid-turn" marker and persist.
        session.TurnInProgress = false;
        ConversationPersistence.AppendJournal(session.Id, "TURN END",
            $"Turn finished (LastTurnHadError={session.Runtime.LastTurnHadError}).");
        ConversationPersistence.SaveSession(session);

        BumpSessionToTopAndRefreshTime(session);

        if (ReferenceEquals(session, _current))
            RefreshChromeForActiveSession();

        ShowDoneBadgeIfInactive(session);

        NotifyTurnFinishedIfWindowInactive(session);
    }
    /// <summary>
    /// Saves the WHOLE session (session.json overwritten) and returns only when the file is written. Called from the
    /// agent loop thread before the first tool of every round runs. The loop thread is blocked meanwhile, so nothing
    /// mutates the conversation history while the UI thread serializes it. Never throws; gives up after 30 s.
    /// </summary>
    public void CheckpointSessionBlocking(ChatSession session, string reason)
    {
        try
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                ConversationPersistence.SaveSession(session);
                return;
            }

            using var done = new System.Threading.ManualResetEventSlim(false);
            Dispatcher.UIThread.Post(() =>
            {
                try { ConversationPersistence.SaveSession(session); }
                finally { done.Set(); }
            });

            if (!done.Wait(TimeSpan.FromSeconds(30)))
                SessionFileLogger.Log("PERSISTENCE", $"Checkpoint ({reason}) timed out after 30 s for '{session.Title}'.");
        }
        catch (Exception ex)
        {
            SessionFileLogger.LogException($"Checkpoint ({reason}) failed for '{session.Title}'", ex);
        }
    }


    private void NotifyTurnFinishedIfWindowInactive(ChatSession session)
    {
        bool isFocused = IsActive && WindowState != WindowState.Minimized;
        if (isFocused) return;

        string title = "LLM Chat — response ready";
        string body  = string.IsNullOrWhiteSpace(session.Title)
            ? "The assistant finished responding."
            : session.Title;

        ToastNotifier.ShowToast(title, body);
    }

    public void ScrollToBottom() => _messagesScroll.ScrollToEnd();

    public void ScrollToBottom(ChatSession session)
    {
        if (ReferenceEquals(session, _current) && _stickToBottom)
            _messagesScroll.ScrollToEnd();
    }

    public void SendMessageExternal(string message)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => SendMessageExternal(message));
            return;
        }
        _inputBox.Text = message;
        SendCurrentMessage();
    }

    public void SendMessageExternalWithFiles(string message, List<string> files)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => SendMessageExternalWithFiles(message, files));
            return;
        }

        _pendingAttachments.Clear();
        foreach (var f in files)
            if (File.Exists(f)) _pendingAttachments.Add(f);

        RefreshAttachmentStrip();
        _inputBox.Text = message;
        SendCurrentMessage();
    }
}
