using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace GTA.App;

public partial class LlmChatWindow
{

    /// <summary>
    /// Creates the session a dispatched Sub Agent runs in. It is a CHILD of parentSession: ParentSessionId is
    /// saved with it, and the sidebar shows it nested under the dispatching session (also after a restart).
    /// </summary>
    internal ChatSession CreateSubAgentTwinSession(ChatSession parentSession, string task, string modelName)
    {
        int siblingNumber = _sessions.Count(s => s.ParentSessionId == parentSession.Id) + 1;

        var twin = new ChatSession
        {
            Title           = BuildSubAgentTwinTitle(task, siblingNumber),
            IsFullyLoaded   = true,
            ParentSessionId = parentSession.Id,
        };
        twin.Runtime.CurrentTurnModelName = modelName;

        _sessions.Insert(0, twin);

        if (_sidebarRows.ContainsKey(parentSession.Id))
        {
            AddSessionListItem(twin, parent: parentSession);
            RefreshTaskFoldVisibility(parentSession);
        }
        else
        {
            // The dispatcher has no row (should not happen) — keep the Sub Agent reachable as a loose row.
            AddSessionListItem(twin, prependToTop: true);
        }

        EnsureSessionPanelBuilt(twin); // Messages.Count == 0 right now → shows the generic placeholder
        var panel = twin.Runtime.MessagesPanel!;
        panel.Children.Clear(); // replace the placeholder with the real task bubble below

        var taskMsg = new ChatMessage
        {
            Role    = "user",
            Content = "🤖  Dispatched as Sub Agent\n\nTASK:\n" + task,
        };
        twin.Messages.Add(taskMsg);
        panel.Children.Add(BuildMessageBubble(twin, taskMsg));

        BeginSessionTurn(twin);

        // Write-ahead, same as a normal prompt: on disk before the sub agent does anything.
        twin.TurnInProgress = true;
        ConversationPersistence.AppendJournal(twin.Id, "SUB AGENT TASK", task);
        ConversationPersistence.SaveSession(twin);

        return twin;
    }

    internal void FinishSubAgentTwinSession(ChatSession twin, bool success, string message)
    {
        string statusLine = success
            ? $"✅  REPORT (success): {message}"
            : $"✗  REPORT (failure): {message}";

        AddStatusLine(twin, statusLine);

        twin.IsDirty = true;
        FinishSessionTurn(twin);
    }

    /// <summary>"🤖 Sub Agent #3: …" — the parent is NOT in the title any more: the sidebar nests the row under it.</summary>
    private static string BuildSubAgentTwinTitle(string task, int siblingNumber)
    {
        string shortTask = TruncateOneLine(task, 48);
        return $"{ConversationPersistence.SubAgentTitlePrefix} #{siblingNumber}: {shortTask}";
    }

    public void AddSubAgentDispatchWidget(ChatSession session, string twinSessionId, string task, bool success, string message)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => AddSubAgentDispatchWidget(session, twinSessionId, task, success, message));
            return;
        }

        var runtime = session.Runtime;

        StackPanel targetPanel = RoundTarget(runtime, runtime.CurrentTurnContentPanel ?? BeginTurnBubble(session));

        var display = new DisplaySubAgentDispatch
        {
            TwinSessionId = twinSessionId,
            Task          = task,
            Success       = success,
            Message       = message,
            SequenceIndex = runtime.CurrentTurnSequence++,
        };
        runtime.CurrentTurnMessage?.SubAgentDispatches.Add(display);

        targetPanel.Children.Add(BuildSubAgentDispatchWidget(display));

        if (ReferenceEquals(session, _current))
            RefreshInjectButtonLabel(); // the pause just ended too — see AvaloniaAgentLogger.EndSubAgentDispatch

        ScrollToBottom(session);
    }

    internal static Control BuildSubAgentDispatchWidget(DisplaySubAgentDispatch info)
    {
        bool  ok         = info.Success;
        string statusWord = ok ? "success" : "failure";
        var   statusCol  = ok ? Color.FromRgb(100, 200, 140) : Color.FromRgb(235, 105, 105);
        var   violet     = Color.FromRgb(170, 140, 235);
        var   mono       = new FontFamily("Consolas, Courier New, monospace");

        var statusChip = new Border
        {
            Background        = new SolidColorBrush(Color.FromArgb(30, statusCol.R, statusCol.G, statusCol.B)),
            BorderBrush       = new SolidColorBrush(Color.FromArgb(90, statusCol.R, statusCol.G, statusCol.B)),
            BorderThickness   = new Thickness(1),
            CornerRadius      = new CornerRadius(8),
            Padding           = new Thickness(6, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child             = new TextBlock
            {
                Text       = (ok ? "✓ " : "✗ ") + statusWord,
                FontSize   = 10,
                FontFamily = mono,
                FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(statusCol),
            },
        };

        var title = new TextBlock
        {
            Text              = "Sub Agent report",
            FontSize          = 11,
            FontWeight        = FontWeight.SemiBold,
            Foreground        = new SolidColorBrush(violet),
            VerticalAlignment = VerticalAlignment.Center,
        };

        string flat = string.Join(" ",
            (info.Message ?? "").Split(new[] { '\r', '\n' }, System.StringSplitOptions.RemoveEmptyEntries)
                                .Select(l => l.Trim()).Where(l => l.Length > 0))
            .Replace("**", "").Replace("`", "");   // one-line preview: drop markdown markers
        var preview = new TextBlock
        {
            Text              = flat,
            FontSize          = 11,
            Foreground        = ColMuted,
            Opacity           = 0.85,
            TextTrimming      = TextTrimming.CharacterEllipsis,
            MaxLines          = 1,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var arrow = new TextBlock
        {
            Text              = "▸",
            FontSize          = 10,
            Foreground        = ColMuted,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var leftGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"),
        };
        title.Margin   = new Thickness(8, 0, 0, 0);
        preview.Margin = new Thickness(10, 0, 8, 0);
        Grid.SetColumn(statusChip, 0);
        Grid.SetColumn(title,      1);
        Grid.SetColumn(preview,    2);
        Grid.SetColumn(arrow,      3);
        leftGrid.Children.Add(statusChip);
        leftGrid.Children.Add(title);
        leftGrid.Children.Add(preview);
        leftGrid.Children.Add(arrow);

        var toggleArea = new Border
        {
            Background = Brushes.Transparent,   // needed so the whole area is clickable
            Cursor     = new Cursor(StandardCursorType.Hand),
            Padding    = new Thickness(0, 3, 0, 3),
            Child      = leftGrid,
        };
        ToolTip.SetTip(toggleArea,
            "The final report a dispatched Sub Agent sent back. Click to show / hide the task and report.");

        var openBtn = new Button
        {
            Content             = new TextBlock { Text = "↷ Open session" },
            FontSize            = 10,
            FontFamily          = mono,
            BorderThickness     = new Thickness(0),
            Padding             = new Thickness(6, 1, 6, 1),
            MinHeight           = 0,
            VerticalAlignment   = VerticalAlignment.Center,
            Cursor              = new Cursor(StandardCursorType.Hand),
        };
        UiTheme.StyleLinkButton(openBtn, ColScheduledAccent);
        ToolTip.SetTip(openBtn,
            "Open the Sub Agent's own session (it is also listed under this chat in the sidebar) to see exactly what it did — " +
            "purely a UI convenience, no effect on this conversation.");
        openBtn.Click += (_, _) => LlmChatWindow.Instance?.JumpToSubAgentTwinSession(info.TwinSessionId);

        var headerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin            = new Thickness(10, 0, 6, 0),
        };
        Grid.SetColumn(toggleArea, 0);
        Grid.SetColumn(openBtn,    1);
        headerGrid.Children.Add(toggleArea);
        headerGrid.Children.Add(openBtn);

        TextBlock MakeSectionLabel(string text, Color c) => new TextBlock
        {
            Text       = text,
            FontSize   = 9,
            FontFamily = mono,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.FromArgb(200, c.R, c.G, c.B)),
            Margin     = new Thickness(0, 0, 0, 2),
        };
        SelectableTextBlock MakeSectionBody(string text) => new SelectableTextBlock
        {
            Text         = text,
            TextWrapping = TextWrapping.Wrap,
            Foreground   = ColText,
            FontSize     = 12,
            FontFamily   = mono,
            LineHeight   = 18,
            Opacity      = 0.9,
        };

        var bodyPanel = new StackPanel { Spacing = 2 };
        bodyPanel.Children.Add(MakeSectionLabel("TASK", violet));
        bodyPanel.Children.Add(MakeSectionBody(info.Task ?? ""));
        bodyPanel.Children.Add(new Border
        {
            Height     = 1,
            Background = ColDivider,
            Opacity    = 0.5,
            Margin     = new Thickness(0, 6, 0, 6),
        });
        bodyPanel.Children.Add(MakeSectionLabel("REPORT · " + statusWord.ToUpperInvariant(), statusCol));

        // The report is Markdown (bullets, tables, bold, code). Render it with the same renderer as the
        // chat bubbles. Built lazily on first expand, so a big report costs nothing while collapsed.
        var reportPanel = new StackPanel { Spacing = 4 };
        bodyPanel.Children.Add(reportPanel);
        bool reportBuilt = false;
        void EnsureReportBuilt()
        {
            if (reportBuilt) return;
            reportBuilt = true;
            try
            {
                RebuildMarkdownPanel(reportPanel, info.Message ?? "");
            }
            catch
            {
                // Never lose the report because of a rendering problem: fall back to plain text.
                reportPanel.Children.Clear();
                reportPanel.Children.Add(MakeSectionBody(info.Message ?? ""));
            }
        }

        var bodyBorder = new Border
        {
            IsVisible = false,
            Padding   = new Thickness(12, 2, 12, 10),
            Child     = new ScrollViewer
            {
                MaxHeight                     = 360,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility   = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                Content                       = bodyPanel,
            },
        };

        toggleArea.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left) return;
            bool open = !bodyBorder.IsVisible;
            if (open) EnsureReportBuilt();
            bodyBorder.IsVisible = open;
            preview.IsVisible    = !open;
            arrow.Text           = open ? "▾" : "▸";
        };

        var inner = new StackPanel();
        inner.Children.Add(headerGrid);
        inner.Children.Add(bodyBorder);

        var bar = new Border { Width = 3, Background = new SolidColorBrush(statusCol) };

        var cardGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(bar,   0);
        Grid.SetColumn(inner, 1);
        cardGrid.Children.Add(bar);
        cardGrid.Children.Add(inner);

        var card = new Border
        {
            Background          = new SolidColorBrush(Color.FromRgb(30, 27, 50)),
            BorderBrush         = new SolidColorBrush(Color.FromArgb(70, violet.R, violet.G, violet.B)),
            BorderThickness     = new Thickness(1),
            CornerRadius        = new CornerRadius(8),
            ClipToBounds        = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin              = new Thickness(4, 2, 4, 3),
            Child               = cardGrid,
        };

        return card;
    }

    private void JumpToSubAgentTwinSession(string twinSessionId)
    {
        if (string.IsNullOrEmpty(twinSessionId)) return;

        var twin = _sessions.FirstOrDefault(s => s.Id == twinSessionId);
        if (twin == null) return;

        if (_sidebarRows.TryGetValue(twin.Id, out var refs))
            SwitchSession(twin, refs.Item);
    }
}
