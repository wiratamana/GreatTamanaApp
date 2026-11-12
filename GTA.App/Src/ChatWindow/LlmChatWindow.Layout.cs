using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using GTA.Tools;
using GTA.Core;

namespace GTA.App;

public partial class LlmChatWindow
{
    // ── Root ──────────────────────────────────────────────────────────────────

    private Control BuildRootLayout()
    {
        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition(290, GridUnitType.Pixel));
        root.ColumnDefinitions.Add(new ColumnDefinition(1,   GridUnitType.Pixel));
        root.ColumnDefinitions.Add(new ColumnDefinition(1,   GridUnitType.Star));

        var sidebar  = BuildSidebar();
        var divider  = BuildVerticalDivider();
        var mainArea = BuildMainArea();

        Grid.SetColumn(sidebar,  0);
        Grid.SetColumn(divider,  1);
        Grid.SetColumn(mainArea, 2);

        root.Children.Add(sidebar);
        root.Children.Add(divider);
        root.Children.Add(mainArea);

        return root;
    }

    // ── Sidebar ───────────────────────────────────────────────────────────────

    private Control BuildSidebar()
    {
        var sidebar = new DockPanel { LastChildFill = true };
        sidebar.Background = BgSidebar;

        var newChatBtn = new Button
        {
            Content                    = "+  New Chat",
            HorizontalAlignment        = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            FontSize                   = 13,
            FontWeight                 = FontWeight.SemiBold,
            Padding                    = new Thickness(0, 10, 0, 10),
            Margin                     = new Thickness(12, 12, 12, 12),
        };
        UiTheme.StyleButton(newChatBtn, ColAccent, Brushes.White);
        newChatBtn.Click += (_, _) => CreateNewChat();
        DockPanel.SetDock(newChatBtn, Dock.Top);
        sidebar.Children.Add(newChatBtn);

        var schedulerBtn = new Button
        {
            Content                    = "⏰  Scheduler",
            HorizontalAlignment        = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            FontSize                   = 13,
            FontWeight                 = FontWeight.SemiBold,
            Padding                    = new Thickness(0, 10, 0, 10),
            Margin                     = new Thickness(12, 0, 12, 12),
        };
        UiTheme.StyleButton(schedulerBtn, ColScheduledBadgeBg, Brushes.White);
        ToolTip.SetTip(schedulerBtn, "Schedule a prompt to run automatically at a future date/time");
        schedulerBtn.Click += (_, _) => OpenSchedulerWindow();
        DockPanel.SetDock(schedulerBtn, Dock.Top);
        sidebar.Children.Add(schedulerBtn);

        var taskBtn = new Button
        {
            Content                    = "📋  Task",
            HorizontalAlignment        = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            FontSize                   = 13,
            FontWeight                 = FontWeight.SemiBold,
            Padding                    = new Thickness(0, 10, 0, 10),
            Margin                     = new Thickness(12, 0, 12, 12),
        };
        UiTheme.StyleButton(taskBtn, ColForkAccent, Brushes.White);
        ToolTip.SetTip(taskBtn, "Build a multi-step Task: a container that runs prompts and delays in order");
        taskBtn.Click += (_, _) => OpenTaskBuilderWindow();
        DockPanel.SetDock(taskBtn, Dock.Top);
        sidebar.Children.Add(taskBtn);
#if CO_WORKER_USE // Settings (encrypted credentials): co-worker build only
        var settingsBtn = new Button
        {
            Content                    = "⚙  Settings",
            HorizontalAlignment        = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            FontSize                   = 13,
            FontWeight                 = FontWeight.SemiBold,
            Padding                    = new Thickness(0, 10, 0, 10),
            Margin                     = new Thickness(12, 0, 12, 12),
        };
        UiTheme.StyleButton(settingsBtn, BgAiBubble, ColText,
            hoverBackground: BgHoverItem, pressedBackground: BgActiveItem, borderBrush: ColDivider);
        ToolTip.SetTip(settingsBtn, "Enter or change your Jira / primary account / mail account credentials (stored encrypted)");
        settingsBtn.Click += async (_, _) => await OpenSettingsWindowAsync();
        DockPanel.SetDock(settingsBtn, Dock.Top);
        sidebar.Children.Add(settingsBtn);
#endif


#if !CO_WORKER_USE // Issue Watcher (git + path_issue_watcher_repo): removed in the co-worker build
        var issueWatcherBtn = new Button
        {
            Content                    = "▶  Start Issue Watcher",
            HorizontalAlignment        = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            FontSize                   = 13,
            FontWeight                 = FontWeight.SemiBold,
            Padding                    = new Thickness(0, 10, 0, 10),
            Margin                     = new Thickness(12, 0, 12, 4),
        };
        UiTheme.StyleButton(issueWatcherBtn, ColDoneBadgeBg, Brushes.White);
        ToolTip.SetTip(issueWatcherBtn,
            $"Poll open GitHub issues in '{IssueWatcherRepoPath}' every minute and auto-dispatch " +
            "a Task for any issue with new activity (new comments/edits)");
        issueWatcherBtn.Click += (_, _) => ToggleIssueWatcher();
        _issueWatcherButton = issueWatcherBtn;
        DockPanel.SetDock(issueWatcherBtn, Dock.Top);
        sidebar.Children.Add(issueWatcherBtn);

        var issueWatcherStatus = new TextBlock
        {
            Text         = "Stopped.",
            FontSize     = 10,
            Foreground   = ColMuted,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(16, 0, 16, 12),
        };
        _issueWatcherStatusLabel = issueWatcherStatus;
        DockPanel.SetDock(issueWatcherStatus, Dock.Top);
        sidebar.Children.Add(issueWatcherStatus);
#endif // !CO_WORKER_USE (Issue Watcher UI)

        var sectionLabel = new TextBlock
        {
            Text       = "RECENT CHATS",
            FontSize   = 10,
            FontWeight = FontWeight.SemiBold,
            Foreground = ColSideMeta,
            Margin     = new Thickness(20, 4, 20, 6),
        };
        DockPanel.SetDock(sectionLabel, Dock.Top);
        sidebar.Children.Add(sectionLabel);

        var sep = BuildHorizontalDivider(new Thickness(0, 0, 0, 6));
        DockPanel.SetDock(sep, Dock.Top);
        sidebar.Children.Add(sep);

        var listScroll = new ScrollViewer
        {
            Content = _sessionListPanel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
        };
        sidebar.Children.Add(listScroll);

        return sidebar;
    }

    // ── Main chat area ────────────────────────────────────────────────────────

    private Control BuildMainArea()
    {
        var mainArea = new DockPanel { LastChildFill = true };
        mainArea.Background = BgMain;

#if !CO_WORKER_USE // LLM provider selector: removed in the co-worker build
        // ── Backend (LLM provider) ComboBox ───────────────────────────────────
        _backendSelectorCombo = new ComboBox
        {
            MinWidth          = 130,
            FontSize          = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(8, 0, 0, 0),
        };
        UiTheme.StyleComboBox(_backendSelectorCombo, BgAiBubble, ColText,
            hoverBackground: BgHoverItem, pressedBackground: BgActiveItem, borderBrush: ColDivider);
        foreach (string backend in BACKENDS)
            _backendSelectorCombo.Items.Add(backend);
        _backendSelectorCombo.SelectedItem = _selectedBackend;

        _backendSelectorCombo.SelectionChanged += (_, _) =>
        {
            if (_backendSelectorCombo.SelectedItem is string name)
            {
                _selectedBackend = name;
                AppendLog($"[Backend] Switched to '{name}'");
            }
        };

        var backendLabel = new TextBlock
        {
            Text              = "LLM:",
            FontSize          = 11,
            Foreground        = ColMuted,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(0, 0, 4, 0),
        };

        var backendPanel = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment   = VerticalAlignment.Center,
            Margin              = new Thickness(0, 0, 12, 0),
        };
        backendPanel.Children.Add(backendLabel);
        backendPanel.Children.Add(_backendSelectorCombo);
#endif // !CO_WORKER_USE (LLM provider selector)

        _injectButton = new Button
        {
            Content           = "💉 Inject",
            FontSize          = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(8, 0, 0, 0),
            Padding           = new Thickness(10, 6, 10, 6),
        };
        UiTheme.StyleButton(_injectButton, BgAiBubble, ColText,
            hoverBackground: BgHoverItem, pressedBackground: BgActiveItem, borderBrush: ColDivider);
        ToolTip.SetTip(_injectButton,
            "Queue text/files/tools to inject into the AI's running turn, starting next round " +
            "(not sent immediately)");
        _injectButton.Click += (_, _) => OpenInjectionWindow();
        // ⏸ Pause / ⏳ Pausing… / ▶ Resume for EVERYTHING (all running chats and Tasks). See LlmChatWindow.PauseDump.cs.
        _globalPauseButton = new Button
        {
            Content           = "⏸ Pause",
            FontSize          = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(8, 0, 0, 0),
            Padding           = new Thickness(10, 6, 10, 6),
            IsEnabled         = false,
        };
        UiTheme.StyleButton(_globalPauseButton, BgAiBubble, ColText,
            hoverBackground: BgHoverItem, pressedBackground: BgActiveItem, borderBrush: ColDivider);
        _globalPauseButton.Click += (_, _) => OnGlobalPauseClicked();

        // ── Header: row 1 = [LLM selector (not in co-worker build) | Inject], row 2 = Title ─
        var topRow = new Grid();
        topRow.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        topRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
#if !CO_WORKER_USE
        Grid.SetColumn(backendPanel, 0);
        topRow.Children.Add(backendPanel);
#endif
        // Top-right button group: [🧠 Memory (developer build only) | 💉 Inject]
        var rightButtons = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment   = VerticalAlignment.Center,
        };
#if !CO_WORKER_USE
        rightButtons.Children.Add(BuildMemoryDebugButton());
#endif
        rightButtons.Children.Add(_globalPauseButton);   // left of 💉 Inject
        rightButtons.Children.Add(_injectButton);
        Grid.SetColumn(rightButtons, 1);
        topRow.Children.Add(rightButtons);

        _headerTitle.Margin       = new Thickness(0, 10, 0, 0);
        _headerTitle.TextTrimming = TextTrimming.CharacterEllipsis;

        var headerStack = new StackPanel();
        headerStack.Children.Add(topRow);
        headerStack.Children.Add(_headerTitle);

        var header = new Border
        {
            Background      = BgMain,
            BorderBrush     = ColDivider,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding         = new Thickness(24, 14, 24, 14),
            Child           = headerStack,
        };
        DockPanel.SetDock(header, Dock.Top);
        mainArea.Children.Add(header);

        var inputArea = BuildInputArea();
        DockPanel.SetDock(inputArea, Dock.Bottom);
        mainArea.Children.Add(inputArea);

        mainArea.Children.Add(_messagesScroll);

        return mainArea;
    }

    // ── Input area ────────────────────────────────────────────────────────────

    private Control BuildInputArea()
    {
        var wrapper = new Border
        {
            Background      = BgInput,
            BorderBrush     = ColDivider,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding         = new Thickness(20, 14, 20, 18),
        };

        var hint = new TextBlock
        {
            Text       = "Press Enter to send  ·  Shift+Enter for new line  ·  📎, Ctrl+V, or drag & drop to attach an image/text file  ·  ⑂ to fork this chat and send there",
            FontSize   = 10,
            Foreground = ColMuted,
            Margin     = new Thickness(0, 0, 0, 6),
        };

        var attachBtn = new Button
        {
            Content           = "📎",
            FontSize          = 18,
            Width             = 44,
            Height            = 44,
            BorderThickness   = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin            = new Thickness(0, 0, 4, 0),
        };
        UiTheme.StyleLinkButton(attachBtn, ColMuted);
        ToolTip.SetTip(attachBtn, "Attach an image or text file (or drag & drop one onto this area)");
        attachBtn.Click += async (_, _) => await OpenFilePickerAsync();

        var inputRow = new Grid();
        inputRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        inputRow.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        inputRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        inputRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        inputRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        Grid.SetColumn(attachBtn,       0);
        Grid.SetColumn(_inputBox,       1);
        Grid.SetColumn(_sendButton,     2);
        Grid.SetColumn(_forkSendButton, 3);
        Grid.SetColumn(_pauseButton!,   3);
        Grid.SetColumn(_retryButton,    4);
        inputRow.Children.Add(attachBtn);
        inputRow.Children.Add(_inputBox);
        inputRow.Children.Add(_sendButton);
        inputRow.Children.Add(_forkSendButton);
        inputRow.Children.Add(_pauseButton!);   // same cell as ⑂ Fork: only one of the two is visible (see RefreshSessionPauseButton)
        inputRow.Children.Add(_retryButton);

        _attachmentStripBorder = new Border
        {
            IsVisible       = false,
            Background      = BgAiBubble,
            CornerRadius    = new CornerRadius(8),
            Padding         = new Thickness(8, 6, 8, 6),
            Margin          = new Thickness(0, 0, 0, 8),
            Child           = new ScrollViewer
            {
                Content = _attachmentInnerPanel,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility   = ScrollBarVisibility.Disabled,
                MaxHeight = 60,
            },
        };

        var innerStack = new StackPanel();
        innerStack.Children.Add(hint);
        innerStack.Children.Add(_attachmentStripBorder);
        innerStack.Children.Add(inputRow);

        wrapper.Child = innerStack;

        DragDrop.SetAllowDrop(wrapper, true);
        wrapper.AddHandler(DragDrop.DragEnterEvent, (_, e) =>
        {
            if (e.Data.Contains(DataFormats.Files))
                wrapper.BorderBrush = ColAccent;
        });
        wrapper.AddHandler(DragDrop.DragLeaveEvent, (_, _) => wrapper.BorderBrush = ColDivider);
        wrapper.AddHandler(DragDrop.DragOverEvent, OnInputAreaDragOver);
        wrapper.AddHandler(DragDrop.DropEvent, (s, e) =>
        {
            OnInputAreaDrop(s, e);
            wrapper.BorderBrush = ColDivider;
        });

        return wrapper;
    }

    // ── Shared divider helpers ────────────────────────────────────────────────

    private static Border BuildVerticalDivider() => new Border
    {
        Background = ColDivider,
        Width      = 1,
    };

    private static Border BuildHorizontalDivider(Thickness margin = default) => new Border
    {
        Background = ColDivider,
        Height     = 1,
        Margin     = margin,
    };
}
