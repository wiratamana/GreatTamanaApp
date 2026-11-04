using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GTA.Core;
using GTA.Tools;

namespace GTA.App;

public partial class LlmChatWindow : Window
{
    // ── Singleton ─────────────────────────────────────────────────────────────
    private static LlmChatWindow? _instance;
    public  static LlmChatWindow? Instance => _instance;

    // ── Colour palette (dark, Claude-inspired) ────────────────────────────────
    // Centralised here so colour-theming changes are a single-file operation.
    internal static readonly SolidColorBrush BgSidebar      = new(Color.FromRgb(31,  31,  43));
    internal static readonly SolidColorBrush BgMain         = new(Color.FromRgb(22,  22,  30));
    internal static readonly SolidColorBrush BgInput        = new(Color.FromRgb(28,  28,  38));
    internal static readonly SolidColorBrush BgUserBubble   = new(Color.FromRgb(88, 101, 242));
    internal static readonly SolidColorBrush BgAiBubble     = new(Color.FromRgb(44,  44,  60));
    internal static readonly SolidColorBrush BgHoverItem    = new(Color.FromRgb(48,  48,  65));
    internal static readonly SolidColorBrush BgActiveItem   = new(Color.FromRgb(60,  60,  80));
    internal static readonly SolidColorBrush BgThinking     = new(Color.FromRgb(30,  28,  48));
    internal static readonly SolidColorBrush ColDivider     = new(Color.FromRgb(55,  55,  72));
    internal static readonly SolidColorBrush ColText        = new(Color.FromRgb(225, 225, 235));
    internal static readonly SolidColorBrush ColMuted       = new(Color.FromRgb(130, 130, 155));

    // Sidebar-only text colours. ColMuted is too dark on BgSidebar, so the chat list uses these lighter ones.
    internal static readonly SolidColorBrush ColSideTitle   = new(Color.FromRgb(236, 237, 247));
    internal static readonly SolidColorBrush ColSideChild   = new(Color.FromRgb(210, 213, 235));
    internal static readonly SolidColorBrush ColSideMeta    = new(Color.FromRgb(158, 162, 192));
    internal static readonly SolidColorBrush ColSideGuide   = new(Color.FromRgb(74,  76, 104));
    internal static readonly SolidColorBrush ColSideActive  = new(Color.FromRgb(52,  54,  82));
    internal static readonly SolidColorBrush ColAccent      = new(Color.FromRgb(88, 101, 242));
    internal static readonly SolidColorBrush ColDisabled    = new(Color.FromRgb(55,  55,  75));
    internal static readonly SolidColorBrush ColThinkLabel  = new(Color.FromRgb(160, 140, 210));
    internal static readonly SolidColorBrush ColHRule       = new(Color.FromRgb(80,  80, 110));
    internal static readonly SolidColorBrush BgCodeBlock    = new(Color.FromRgb(18,  18,  28));
    internal static readonly SolidColorBrush BgCodeHeader   = new(Color.FromRgb(35,  35,  52));
    internal static readonly SolidColorBrush ColCodeText    = new(Color.FromRgb(180, 210, 255));

    // Used by the "Fork & Send" button
    internal static readonly SolidColorBrush ColForkAccent  = new(Color.FromRgb(45, 150, 160));

    // Used by the Send/Stop button while a turn is running, and by the Retry button while it is clickable
    internal static readonly SolidColorBrush ColRunningRed  = new(Color.FromRgb(190, 50, 50));
    internal static readonly SolidColorBrush ColRetryAmber  = new(Color.FromRgb(190, 130, 30));

    // Sidebar "processing…" / "DONE" badge colours
    internal static readonly SolidColorBrush ColProcessing  = new(Color.FromRgb(230, 190, 80));
    internal static readonly SolidColorBrush ColDoneBadgeBg = new(Color.FromRgb(40, 130, 90));
    internal static readonly SolidColorBrush ColDoneBadgeFg = new(Color.FromRgb(220, 255, 235));

    internal static readonly SolidColorBrush ColScheduledBadgeBg = new(Color.FromRgb(90, 70, 150));
    internal static readonly SolidColorBrush ColScheduledBadgeFg = new(Color.FromRgb(225, 215, 250));
    internal static readonly SolidColorBrush ColScheduledAccent  = new(Color.FromRgb(190, 150, 230));

    // Colours used ONLY by a Task's per-step SIDEBAR rows
    internal static readonly SolidColorBrush ColTaskRunningBadgeBg = new(Color.FromRgb(150, 110, 30));
    internal static readonly SolidColorBrush ColTaskRunningBadgeFg = new(Color.FromRgb(255, 235, 200));

    // ── UI fields ─────────────────────────────────────────────────────────────
    private readonly StackPanel   _sessionListPanel;
    private readonly ScrollViewer _messagesScroll;
    private readonly TextBox      _inputBox;
    private readonly Button       _sendButton;
    private readonly Button       _retryButton;
    private readonly Button       _forkSendButton;
    private readonly TextBlock    _headerTitle;

    // "Stick to bottom" auto-scroll tracking
    private bool _stickToBottom = true;

    // ── Backend (LLM provider) selector ──────────────────────────────────────
    private string    _selectedBackend      = "Claude";
    private ComboBox? _backendSelectorCombo;

    /// <summary>Well-known backend display names shown in the UI ComboBox.</summary>
    private static readonly string[] BACKENDS = ["Claude", "GPT (gpt-6.1-sol)", "Gemini (gemini-3.7-flash)"];

    // ── Agent selector ────────────────────────────────────────────────────────
    private AgentDefinition _selectedAgent = AgentRegistry.DefaultAgent;

    /// <summary>When non-null, overrides _selectedAgent for the NEXT message ONLY (cleared after RunLlmAsync begins)</summary>
    private AgentDefinition? _pendingAgentOverride = null;

    private ComboBox? _agentSelectorCombo;

    // ── Attachment UI ─────────────────────────────────────────────────────────
    private readonly List<string> _pendingAttachments  = new();
    private readonly StackPanel   _attachmentInnerPanel = new()
    {
        Orientation = Orientation.Horizontal,
        Spacing     = 8,
    };
    private Border? _attachmentStripBorder;

    // Pre-load Tools popup (see LlmChatWindow.ToolPreload.cs)
    private ToolPreloadPopupWindow? _toolPreloadPopup;
    private ChatSession?            _toolPreloadPopupSession;
    private Button?                 _preloadToolsButton;

    private Button? _injectButton;

    // "⏸ Pause" / "▶ Resume" header button (see LlmChatWindow.Pause.cs)
    private Button? _pauseButton;


    // ── Data ──────────────────────────────────────────────────────────────────
    private readonly List<ChatSession> _sessions = new();
    private ChatSession? _current;
    private Border?      _activeSessionItem;

    // Sidebar row control references, keyed by session id
    private readonly Dictionary<string, SidebarRowRefs> _sidebarRows = new();

    // Ticks every 100ms so the ACTIVE session's round-timer badge and the sidebar "processing" pulse stay live
    private readonly DispatcherTimer _roundTickTimer;

    // ── Log queue ─────────────────────────────────────────────────────────────
    private readonly Queue<string> _logQueue = new();
    private const int MaxLogLines = 200;


    // Constructor
    public LlmChatWindow()
    {
        _instance = this;

        Title     = "LLM Chat";
        Width     = 1100;
        Height    = 750;
        MinWidth  = 700;
        MinHeight = 480;
        Background = BgMain;

        _sessionListPanel = new StackPanel { Spacing = 2, Margin = new Thickness(8, 0, 8, 8) };

        _messagesScroll = new ScrollViewer
        {
            Content = null,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
        };

        // Drag-select / Ctrl+C / Ctrl+A across ALL text blocks of a chat
        // (each paragraph, code block, table cell is its own SelectableTextBlock).
        CrossBlockSelection.Attach(_messagesScroll);

        // Track whether the viewport is currently at the bottom (see the _stickToBottom field comment above).
        _messagesScroll.ScrollChanged += (_, _) =>
        {
            double maxOffsetY = Math.Max(0, _messagesScroll.Extent.Height - _messagesScroll.Viewport.Height);
            _stickToBottom = _messagesScroll.Offset.Y >= maxOffsetY - 4;

            if (_current != null)
                _current.Runtime.LastScrollOffsetY = _messagesScroll.Offset.Y;
        };

        // Restore scroll position when the window regains OS focus
        Activated += (_, _) =>
        {
            var session = _current;
            if (session == null) return;

            double target = session.Runtime.LastScrollOffsetY;
            if (target < 0) return; // never recorded yet — nothing to restore

            void Restore()
            {
                if (!ReferenceEquals(_current, session)) return; // user already switched away
                _messagesScroll.Offset = new Vector(_messagesScroll.Offset.X, target);
            }

            Dispatcher.UIThread.Post(Restore, DispatcherPriority.Loaded);
            Dispatcher.UIThread.Post(Restore, DispatcherPriority.Background);
        };

        _inputBox = new TextBox
        {
            Watermark     = "Type a message…  (Enter to send, Shift+Enter for new line)",
            AcceptsReturn = true,
            MaxHeight     = 140,
            Padding       = new Thickness(14, 10, 14, 10),
            FontSize      = 14,
            // Never let long unbroken lines force this box (and the whole input row/window) to grow wider
            TextWrapping  = TextWrapping.Wrap,
        };
        UiTheme.StyleTextBox(_inputBox, BgAiBubble, ColText, borderBrush: ColDivider, focusBorderBrush: ColAccent);

        _sendButton = new Button
        {
            Content             = "➤",
            FontSize            = 18,
            Width               = 44,
            Height              = 44,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment   = VerticalAlignment.Center,
            Margin              = new Thickness(8, 0, 0, 0),
            VerticalAlignment   = VerticalAlignment.Bottom,
        };
        UiTheme.StyleButton(_sendButton, ColAccent, Brushes.White);

        _headerTitle = new TextBlock
        {
            Text       = "Select or start a new chat",
            FontSize   = 16,
            FontWeight = FontWeight.SemiBold,
            Foreground = ColText,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // Retry button
        _retryButton = new Button
        {
            Content             = "⟳",
            FontSize            = 18,
            Width               = 44,
            Height              = 44,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment   = VerticalAlignment.Center,
            Margin              = new Thickness(6, 0, 0, 0),
            VerticalAlignment   = VerticalAlignment.Bottom,
            IsEnabled           = false,
        };
        UiTheme.StyleButton(_retryButton, ColDisabled, ColMuted,
            disabledBackground: ColDisabled, disabledForeground: ColMuted);
        ToolTip.SetTip(_retryButton,
            "Retry now — cancels the current connection attempt and immediately starts a new one");
        _retryButton.Click += (_, _) => _current?.Runtime.CurrentCancelAttempt?.Invoke();

        // Fork & Send button
        _forkSendButton = new Button
        {
            Content             = "⑂",
            FontSize            = 18,
            Width               = 44,
            Height              = 44,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment   = VerticalAlignment.Center,
            Margin              = new Thickness(6, 0, 0, 0),
            VerticalAlignment   = VerticalAlignment.Bottom,
        };
        UiTheme.StyleButton(_forkSendButton, ColForkAccent, Brushes.White);
        ToolTip.SetTip(_forkSendButton,
            "Fork this chat into a new session and send there — leaves this chat untouched");
        _forkSendButton.Click += (_, _) => ForkAndSendCurrentMessage();

        // Sidebar-pulse / scheduler / pause ticker
        _roundTickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _roundTickTimer.Tick += (_, _) =>
        {
            TickSidebarProcessingPulse();

            // Scheduler (see LlmChatWindow.Scheduler.cs)
            CheckScheduledPrompts();
            UpdateScheduledCountdownLabel();

            // Task scheduling (see LlmChatWindow.Task.cs)
            CheckScheduledTasks();
            UpdateTaskScheduledCountdownLabel();

            // "⏸ Pause" (see LlmChatWindow.Pause.cs)
            TickPauseVisuals();
        };
        _roundTickTimer.Start();

        _sendButton.Click += (_, _) =>
        {
            if (_current != null && _current.IsPaused)
                ResumeSession(_current);
            else if (_current != null && _current.Runtime.TaskCurrentUnitIndex >= 0)
                CancelRunningTask(_current);
            else if (IsLlmRunning)
                CancelCurrentLlm();
            else
                SendCurrentMessage();
        };


        _inputBox.AddHandler(
            InputElement.KeyDownEvent,
            (object? _, KeyEventArgs e) =>
            {
                if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                {
                    e.Handled = true;
                    SendCurrentMessage();
                    return;
                }

                if (e.Key == Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Control))
                {
                    e.Handled = true;
                    _ = HandleClipboardPasteAsync();
                }
            },
            handledEventsToo: false,
            routes: RoutingStrategies.Tunnel);

        Content = BuildRootLayout();
        LoadSavedSessions();
    }

    // ── Public property ───────────────────────────────────────────────────────

    /// <summary>True while the CURRENTLY ACTIVE/VISIBLE session has an LLM turn in flight.</summary>
    public bool IsLlmRunning => _current?.Runtime.IsProcessing ?? false;

    /// <summary>Build a fresh, empty message-bubble StackPanel with the same styling the main chat area has always used.</summary>
    private static StackPanel NewMessagesPanel() => new StackPanel
    {
        Spacing = 12,
        Margin  = new Thickness(24, 20, 24, 20),
    };

    internal string CurrentEvidenceDir =>
        _current != null
            ? ConversationPersistence.EvidenceDir(_current.Id)
            : System.IO.Path.Combine(AppContext.BaseDirectory, "sessions", "_unsaved", "evidence");

    internal string CurrentPastedImagesDir =>
        _current != null
            ? ConversationPersistence.PastedImagesDir(_current.Id)
            : System.IO.Path.Combine(AppContext.BaseDirectory, "sessions", "_unsaved", "pasted");
}
