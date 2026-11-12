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
        {
            var log = runtime.CurrentRoundContentPanel.Children;
            log.Add(statusBlock);
            // MEMORY: the raw log of one round is a debugging view that is never saved; keep only its newest lines.
            const int MaxRoundLogLines = 60;
            if (log.Count > MaxRoundLogLines)
                log.RemoveRange(0, log.Count - MaxRoundLogLines);
        }
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

        // "running" = the big button is ⏹ Stop. A chat that is PAUSED still runs (its loop holds at the end of a round),
        // and a chat that is only paused in the pause file (after an app start) counts as running too: it can be stopped.
        var  owner   = session == null ? null : ResolvePauseOwner(session);
        bool running = session != null &&
                       (IsSessionRunning(session) || (owner != null && owner.Runtime.RestoredPause != null));

        // While paused the input box stays usable: the user can queue 💉 injections for the next round.
        bool paused = owner?.IsPaused ?? false;

        _inputBox.IsEnabled   = paused || !running;
        _sendButton.IsEnabled = true; // always clickable (stop / send)
        _sendButton.Content   = running ? "⏹" : "➤";
        ToolTip.SetTip(_sendButton, running
            ? (paused ? "Stop — throws the paused work away" : "Stop")
            : null);

        UiTheme.StyleButton(_sendButton,
            running ? ColRunningRed : ColAccent,
            Brushes.White);

        // Retry only works while a connection attempt is in flight. A paused chat holds BETWEEN rounds, so it is inactive.
        bool canRetryNow = running && session?.Runtime.CurrentCancelAttempt != null;
        _retryButton.IsEnabled = canRetryNow;
        UiTheme.StyleButton(_retryButton,
            canRetryNow ? ColRetryAmber : ColDisabled,
            canRetryNow ? Brushes.White : ColMuted,
            disabledBackground: ColDisabled,
            disabledForeground: ColMuted);

        _sessionPauseState = -1;   // force a repaint of the ⏸ / ▶ button below
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

        // A turn that was resumed after an app restart continues in the same bubble: the loop only knows the usage of
        // the part AFTER the resume, so the part before it (saved in the bubble) is added to the final total.
        if (isFinal) usage = runtime.TurnUsageBase + usage;

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
            {
                ToolTip.SetTip(runtime.CurrentRoundBadge, BuildRoundTooltip(roundRecord));
                ApplyRoundTtlMark(runtime.CurrentRoundBadge, roundRecord);   // amber pill when 1h cache TTL was used
            }
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

    // ── Cache keep-alive: visible note on the round ───────────────────────────────

    private static readonly SolidColorBrush KaOkBg      = new(Color.FromArgb(38,  70, 200, 120));
    private static readonly SolidColorBrush KaOkBorder  = new(Color.FromArgb(150, 90, 210, 140));
    private static readonly SolidColorBrush KaOkFg      = new(Color.FromRgb(130, 225, 165));
    private static readonly SolidColorBrush KaWarnBg    = new(Color.FromArgb(45,  235, 170, 50));
    private static readonly SolidColorBrush KaWarnBorder= new(Color.FromArgb(160, 240, 185, 80));
    private static readonly SolidColorBrush KaWarnFg    = new(Color.FromRgb(245, 200, 100));
    private static readonly SolidColorBrush KaBadBg     = new(Color.FromArgb(45,  230, 80, 80));
    private static readonly SolidColorBrush KaBadBorder = new(Color.FromArgb(160, 240, 110, 110));
    private static readonly SolidColorBrush KaBadFg     = new(Color.FromRgb(245, 140, 140));

    /// <summary>
    /// One-line text of the keep-alive result of a round. Used by the visible note AND the round's hover popup.
    /// Returns null when the round had no keep-alive ping at all.
    /// </summary>
    internal static string? BuildKeepAliveText(DisplayRound r)
    {
        if (r.KeepAlivePings <= 0 && r.KeepAliveFailures <= 0) return null;

        string text = r.KeepAlivePings > 0
            ? $"♻ Cache kept warm · {r.KeepAlivePings} ping{(r.KeepAlivePings == 1 ? "" : "s")} · " +
              $"read {r.KeepAliveReadTokens:N0} tokens · ${r.KeepAliveCostUsd:0.0000}"
            : "⚠ Cache keep-alive did not work";

        if (r.KeepAliveFailures > 0)
            text += $" · ⚠ {r.KeepAliveFailures} failed" + (string.IsNullOrEmpty(r.KeepAliveLastError) ? "" : $": {r.KeepAliveLastError}");
        return text;
    }

    /// <summary>
    /// Called (from any thread) after EACH cache keep-alive ping. Shows a green note under the long-running tool
    /// ("♻ Cache kept warm · 2 pings · read 116,391 tokens · $0.0117"), turns it amber/red when a ping failed or read
    /// nothing from the cache, colours the round number, and adds the same line to the round's hover popup.
    /// The numbers are saved with the chat (DisplayRound). The ping cost is NOT in the round's $ (it is in the session total).
    /// </summary>
    public void AddCacheKeepAliveNote(ChatSession session, int pingNumber, bool ok, TokenUsage usage, string? error)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => AddCacheKeepAliveNote(session, pingNumber, ok, usage, error));
            return;
        }

        var runtime = session.Runtime;
        var record  = runtime.CurrentRoundRecord;
        if (record == null) return;

        // A ping that worked but read NOTHING from the cache did not help: the cache was already gone, or the prefix differs.
        bool hit = ok && usage.CacheReadInputTokens > 0;

        if (ok)
        {
            record.KeepAliveReadTokens += usage.CacheReadInputTokens;
            if (ModelPricing.TryComputeCost(runtime.CurrentTurnModelName, usage, out double pingCost))
                record.KeepAliveCostUsd += pingCost;
        }
        if (hit)
        {
            record.KeepAlivePings++;
        }
        else
        {
            record.KeepAliveFailures++;
            record.KeepAliveLastError = ok
                ? "the ping read nothing from the cache (it was already expired, or the history changed)"
                : error;
        }

        string text = BuildKeepAliveText(record) ?? "";
        bool allBad  = record.KeepAlivePings == 0;
        bool someBad = record.KeepAliveFailures > 0;

        var bg     = allBad ? KaBadBg     : someBad ? KaWarnBg     : KaOkBg;
        var border = allBad ? KaBadBorder : someBad ? KaWarnBorder : KaOkBorder;
        var fg     = allBad ? KaBadFg     : someBad ? KaWarnFg     : KaOkFg;

        var note = runtime.CurrentKeepAliveNote;
        if (note == null)
        {
            note = new Border
            {
                CornerRadius        = new CornerRadius(6),
                BorderThickness     = new Thickness(1),
                Padding             = new Thickness(8, 3, 8, 3),
                Margin              = new Thickness(0, 2, 0, 2),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child               = new TextBlock
                {
                    FontSize     = 11,
                    FontFamily   = new FontFamily("Consolas, Courier New, monospace"),
                    TextWrapping = TextWrapping.Wrap,
                },
            };
            runtime.CurrentKeepAliveNote = note;

            if (runtime.CurrentRoundItemsPanel != null)
                runtime.CurrentRoundItemsPanel.Children.Add(note);
            else if (runtime.CurrentTurnContentPanel != null)
                runtime.CurrentTurnContentPanel.Children.Add(note);
            else
                runtime.MessagesPanel?.Children.Add(note);
        }

        note.Background  = bg;
        note.BorderBrush = border;
        if (note.Child is TextBlock tb)
        {
            tb.Text       = text;
            tb.Foreground = fg;
        }
        ToolTip.SetTip(note,
            "Cache keep-alive: while a long tool runs, a tiny request is sent every few minutes. " +
            "It reads the cached history, so the 5-minute cache does not expire and the next round does not " +
            "have to write the whole history again.");

        if (runtime.CurrentRoundBadge != null)
        {
            ToolTip.SetTip(runtime.CurrentRoundBadge, BuildRoundTooltip(record));
            ApplyRoundKeepAliveMark(runtime.CurrentRoundBadge, record);   // green round number
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
        {
            // Show the 1-hour part when there is one: it is billed at the higher CacheWrite1h price.
            string writeText = usage.CacheCreation1hInputTokens <= 0
                ? $"{usage.CacheCreationInputTokens:N0}"
                : usage.CacheCreation1hInputTokens >= usage.CacheCreationInputTokens
                    ? $"{usage.CacheCreationInputTokens:N0} (1h)"
                    : $"{usage.CacheCreationInputTokens:N0} (1h {usage.CacheCreation1hInputTokens:N0})";
            host.Children.Add(MakeTokenChip("Write", writeText, Color.FromRgb(210, 160, 80)));
        }

        if (usage.CacheTtl1h)
            host.Children.Add(MakeTokenChip("TTL", "1h", Color.FromRgb(245, 195, 90)));

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
                $"Write$={usage.CacheCreationInputTokens:N0} (1h={usage.CacheCreation1hInputTokens:N0}) Cache✓={usage.CacheReadInputTokens:N0} " +
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

        // The turn is over: a pause request / saved pause of it is pointless now (the entry leaves the pause file).
        ClearPauseState(session);

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

        // MEMORY: a turn just ended — the heap is full of its garbage (big JSON strings, widgets). Compact it.
        MemoryGovernor.Request("turn finished");
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
