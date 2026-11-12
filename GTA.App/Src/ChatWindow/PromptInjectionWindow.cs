using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using GTA.Core;

namespace GTA.App;

/// <summary>
/// The "💉 Injection queue" window.
/// - Shows every message that is waiting for the AI's next round.
/// - Lets the user ADD a new message, EDIT one, REMOVE one, MOVE one up/down, or CLEAR all.
/// - NOT modal: the user can leave it open and keep using the chat.
/// - Optional "hold" (default ON): while the window is open the AI waits before its next round,
///   so a message being edited can not be taken half-written. The hold belongs to THIS window only.
///   Nothing else (sub agent finishing, etc.) can release it.
/// </summary>
public sealed class PromptInjectionWindow : Window
{
    private static readonly SolidColorBrush ColDanger = new(Color.FromRgb(230, 120, 120));

    private readonly ChatSessionRuntime _runtime;
    private readonly Action             _onQueueChanged;

    // Queue part
    private readonly StackPanel _queueList;
    private readonly TextBlock  _queueHeader;
    private readonly Button     _clearAllBtn;
    private readonly Border     _emptyState;
    private readonly TextBlock  _statusText;

    // Composer part
    private readonly TextBlock  _composerTitle;
    private readonly TextBlock  _noteText;
    private readonly TextBox    _textBox;
    private readonly WrapPanel  _filesPanel;
    private readonly TextBlock  _toolsHeader;
    private readonly Button     _saveBtn;
    private readonly Button     _cancelEditBtn;
    private readonly CheckBox   _holdCheck;

    private readonly List<string>    _attachedFiles = new();
    private readonly HashSet<string> _selectedTools = new(StringComparer.OrdinalIgnoreCase);

    private string? _editingId;     // null = composing a NEW message
    private bool    _opened;

    private readonly DispatcherTimer _statusTimer;

    public PromptInjectionWindow(string? sessionTitle, ChatSessionRuntime runtime, Action onQueueChanged)
    {
        _runtime        = runtime        ?? throw new ArgumentNullException(nameof(runtime));
        _onQueueChanged = onQueueChanged ?? throw new ArgumentNullException(nameof(onQueueChanged));

        Title = string.IsNullOrEmpty(sessionTitle)
            ? "💉 Injection queue"
            : $"💉 Injection queue — {sessionTitle}";
        Width                 = 640;
        Height                = 780;
        MinWidth              = 480;
        MinHeight             = 560;
        Background            = LlmChatWindow.BgMain;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        // ── Header ─────────────────────────────────────────────────────────
        var titleBlock = new TextBlock
        {
            Text       = "💉 Injection queue",
            FontSize   = 18,
            FontWeight = FontWeight.SemiBold,
            Foreground = LlmChatWindow.ColText,
        };

        var sessionBlock = new TextBlock
        {
            Text         = string.IsNullOrEmpty(sessionTitle) ? "" : sessionTitle,
            FontSize     = 11,
            Foreground   = LlmChatWindow.ColMuted,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin       = new Thickness(0, 2, 0, 0),
            IsVisible    = !string.IsNullOrEmpty(sessionTitle),
        };

        var subtitle = new TextBlock
        {
            Text = "Messages here are NOT sent yet. The AI receives them, in this order, at the start " +
                   "of its next round. You can still edit, reorder or remove them until then.",
            FontSize     = 11,
            Foreground   = LlmChatWindow.ColMuted,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 6, 0, 0),
        };

        _statusText = new TextBlock
        {
            FontSize          = 11,
            Foreground        = LlmChatWindow.ColText,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var statusChip = new Border
        {
            Background          = LlmChatWindow.BgThinking,
            BorderBrush         = LlmChatWindow.ColDivider,
            BorderThickness     = new Thickness(1),
            CornerRadius        = new CornerRadius(10),
            Padding             = new Thickness(10, 3, 10, 3),
            Margin              = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child               = _statusText,
        };

        var headerPanel = new StackPanel { Margin = new Thickness(20, 16, 20, 8) };
        headerPanel.Children.Add(titleBlock);
        headerPanel.Children.Add(sessionBlock);
        headerPanel.Children.Add(subtitle);
        headerPanel.Children.Add(statusChip);
        DockPanel.SetDock(headerPanel, Dock.Top);

        // ── Footer: hold switch + close ────────────────────────────────────
        _holdCheck = new CheckBox
        {
            Content           = "Hold the AI before its next round while this window is open",
            IsChecked         = true,
            FontSize          = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        UiTheme.StyleCheckBox(_holdCheck, LlmChatWindow.ColText);
        ToolTip.SetTip(_holdCheck,
            "ON (recommended): the AI finishes its current round, then waits. Your edits can not be " +
            "taken half-written. Nothing else can release this hold: not a Sub Agent finishing, not a " +
            "tool ending. Only you can, by un-ticking this or closing the window.\n" +
            "OFF: the AI keeps running and takes the queue at its next round start.");
        _holdCheck.IsCheckedChanged += (_, _) => { ApplyHold(); UpdateStatus(); _onQueueChanged(); };

        var closeBtn = new Button
        {
            Content  = "Close",
            FontSize = 12,
            Padding  = new Thickness(18, 6, 18, 6),
        };
        UiTheme.StyleButton(closeBtn, LlmChatWindow.ColDisabled, LlmChatWindow.ColText);
        closeBtn.Click += (_, _) => Close();

        var footer = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin            = new Thickness(20, 8, 20, 14),
        };
        Grid.SetColumn(_holdCheck, 0);
        Grid.SetColumn(closeBtn,   1);
        footer.Children.Add(_holdCheck);
        footer.Children.Add(closeBtn);
        DockPanel.SetDock(footer, Dock.Bottom);

        // ── Queue section ──────────────────────────────────────────────────
        _queueHeader = new TextBlock
        {
            FontSize          = 12,
            FontWeight        = FontWeight.SemiBold,
            Foreground        = LlmChatWindow.ColText,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _clearAllBtn = new Button
        {
            Content         = "Clear all",
            FontSize        = 11,
            Padding         = new Thickness(8, 2, 8, 2),
            BorderThickness = new Thickness(0),
        };
        UiTheme.StyleLinkButton(_clearAllBtn, ColDanger);
        _clearAllBtn.Click += (_, _) =>
        {
            _runtime.ClearInjections();
            if (_editingId != null) ExitEditMode(clearComposer: false, note: null);
        };

        var queueHeaderRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin            = new Thickness(20, 4, 20, 6),
        };
        Grid.SetColumn(_queueHeader, 0);
        Grid.SetColumn(_clearAllBtn, 1);
        queueHeaderRow.Children.Add(_queueHeader);
        queueHeaderRow.Children.Add(_clearAllBtn);

        _queueList = new StackPanel { Spacing = 8 };

        _emptyState = new Border
        {
            BorderBrush     = LlmChatWindow.ColDivider,
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(10),
            Padding         = new Thickness(16, 22, 16, 22),
            Child           = new StackPanel
            {
                Spacing  = 4,
                Children =
                {
                    new TextBlock
                    {
                        Text                = "Nothing queued",
                        FontSize            = 13,
                        FontWeight          = FontWeight.SemiBold,
                        Foreground          = LlmChatWindow.ColText,
                        HorizontalAlignment = HorizontalAlignment.Center,
                    },
                    new TextBlock
                    {
                        Text                = "Write a message below and press \"Add to queue\".",
                        FontSize            = 11,
                        Foreground          = LlmChatWindow.ColMuted,
                        HorizontalAlignment = HorizontalAlignment.Center,
                    },
                },
            },
        };

        var queueHost = new StackPanel { Margin = new Thickness(20, 0, 20, 8) };
        queueHost.Children.Add(_emptyState);
        queueHost.Children.Add(_queueList);

        var queueScroll = new ScrollViewer
        {
            Content                       = queueHost,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
        };

        var queueSection = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(queueHeaderRow, Dock.Top);
        queueSection.Children.Add(queueHeaderRow);
        queueSection.Children.Add(queueScroll);

        // ── Composer ───────────────────────────────────────────────────────
        _composerTitle = new TextBlock
        {
            FontSize          = 12,
            FontWeight        = FontWeight.SemiBold,
            Foreground        = LlmChatWindow.ColText,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _cancelEditBtn = new Button
        {
            Content         = "Cancel edit",
            FontSize        = 11,
            Padding         = new Thickness(8, 2, 8, 2),
            BorderThickness = new Thickness(0),
            IsVisible       = false,
        };
        UiTheme.StyleLinkButton(_cancelEditBtn, LlmChatWindow.ColMuted);
        _cancelEditBtn.Click += (_, _) => ExitEditMode(clearComposer: true, note: null);

        var composerTitleRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(_composerTitle, 0);
        Grid.SetColumn(_cancelEditBtn, 1);
        composerTitleRow.Children.Add(_composerTitle);
        composerTitleRow.Children.Add(_cancelEditBtn);

        _noteText = new TextBlock
        {
            FontSize     = 11,
            Foreground   = LlmChatWindow.ColRetryAmber,
            TextWrapping = TextWrapping.Wrap,
            IsVisible    = false,
            Margin       = new Thickness(0, 4, 0, 0),
        };

        _textBox = new TextBox
        {
            Watermark     = "Type additional info / instructions for the AI…",
            AcceptsReturn = true,
            TextWrapping  = TextWrapping.Wrap,
            MinHeight     = 80,
            MaxHeight     = 150,
            FontSize      = 13,
            Padding       = new Thickness(10, 8, 10, 8),
            Margin        = new Thickness(0, 6, 0, 6),
        };
        UiTheme.StyleTextBox(_textBox, LlmChatWindow.BgAiBubble, LlmChatWindow.ColText,
            borderBrush: LlmChatWindow.ColDivider, focusBorderBrush: LlmChatWindow.ColAccent);

        _textBox.AddHandler(
            InputElement.KeyDownEvent,
            (object? _, KeyEventArgs e) =>
            {
                if (e.Key == Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Control))
                {
                    e.Handled = true;
                    _ = HandleClipboardPasteAsync();
                }
                else if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
                {
                    e.Handled = true;
                    SaveComposer();
                }
            },
            handledEventsToo: false,
            routes: RoutingStrategies.Tunnel);

        var attachBtn = new Button
        {
            Content  = "📎  Attach file…",
            FontSize = 12,
            Padding  = new Thickness(10, 5, 10, 5),
        };
        UiTheme.StyleButton(attachBtn, LlmChatWindow.BgAiBubble, LlmChatWindow.ColText,
            hoverBackground: LlmChatWindow.BgHoverItem, borderBrush: LlmChatWindow.ColDivider);
        attachBtn.Click += async (_, _) => await PickFileAsync();

        _filesPanel = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };

        _toolsHeader = new TextBlock
        {
            FontSize          = 12,
            Foreground        = LlmChatWindow.ColText,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // The tool list itself lives in the shared ToolPreloadPopupWindow (universal picker + "My presets").
        var toolsBtn = new Button
        {
            Content                    = _toolsHeader,
            HorizontalAlignment        = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding                    = new Thickness(12, 9, 12, 9),
            Margin                     = new Thickness(0, 8, 0, 0),
        };
        UiTheme.StyleButton(toolsBtn, LlmChatWindow.BgAiBubble, LlmChatWindow.ColText,
            hoverBackground: LlmChatWindow.BgHoverItem, borderBrush: LlmChatWindow.ColDivider);
        ToolTip.SetTip(toolsBtn, "Optionally load more tools for the AI. They are available starting from the round that receives this message.\nOpens the tool picker, where you can also use your saved presets.");
        toolsBtn.Click += async (_, _) => await OpenToolPickerAsync();

        _saveBtn = new Button
        {
            FontSize          = 12,
            FontWeight        = FontWeight.SemiBold,
            Padding           = new Thickness(18, 7, 18, 7),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        UiTheme.StyleButton(_saveBtn, LlmChatWindow.ColAccent, Brushes.White);
        _saveBtn.Click += (_, _) => SaveComposer();

        var shortcutHint = new TextBlock
        {
            Text              = "Ctrl+Enter = add / save",
            FontSize          = 10,
            Foreground        = LlmChatWindow.ColMuted,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var actionRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin            = new Thickness(0, 10, 0, 0),
        };
        Grid.SetColumn(attachBtn,    0);
        Grid.SetColumn(shortcutHint, 1);
        Grid.SetColumn(_saveBtn,     2);
        shortcutHint.HorizontalAlignment = HorizontalAlignment.Center;
        actionRow.Children.Add(attachBtn);
        actionRow.Children.Add(shortcutHint);
        actionRow.Children.Add(_saveBtn);

        var composerStack = new StackPanel();
        composerStack.Children.Add(composerTitleRow);
        composerStack.Children.Add(_noteText);
        composerStack.Children.Add(_textBox);
        composerStack.Children.Add(_filesPanel);
        composerStack.Children.Add(toolsBtn);
        composerStack.Children.Add(actionRow);

        var composerCard = new Border
        {
            Background      = LlmChatWindow.BgThinking,
            BorderBrush     = LlmChatWindow.ColDivider,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding         = new Thickness(20, 12, 20, 12),
            Child           = composerStack,
        };

        var composerScroll = new ScrollViewer
        {
            Content                       = composerCard,
            MaxHeight                     = 420,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
        };

        var body = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        Grid.SetRow(queueSection,   0);
        Grid.SetRow(composerScroll, 1);
        body.Children.Add(queueSection);
        body.Children.Add(composerScroll);

        var root = new DockPanel { LastChildFill = true };
        root.Children.Add(headerPanel);
        root.Children.Add(footer);
        root.Children.Add(body);
        Content = root;

        UpdateToolsHeader();
        UpdateComposerMode();
        RefreshQueue();

        // ── Lifetime wiring ────────────────────────────────────────────────
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _statusTimer.Tick += (_, _) => UpdateStatus();

        _runtime.InjectionQueueChanged += OnQueueChangedFromAnyThread;

        Opened += (_, _) =>
        {
            _opened = true;
            ApplyHold();
            _statusTimer.Start();
            UpdateStatus();
            _onQueueChanged();
        };

        Closed += (_, _) =>
        {
            _statusTimer.Stop();
            _runtime.InjectionQueueChanged -= OnQueueChangedFromAnyThread;
            _runtime.SetHold(this, false);   // release ONLY this window's own hold
            _onQueueChanged();
        };
    }

    // ── Hold ──────────────────────────────────────────────────────────────

    private void ApplyHold()
    {
        if (!_opened) return;
        _runtime.SetHold(this, _holdCheck.IsChecked == true);
    }

    private void UpdateStatus()
    {
        bool hold = _runtime.HasHold(this);

        if (_runtime.IsPauseActive)
            _statusText.Text = "⏸  The AI is waiting before its next round. Take your time.";
        else if (!_runtime.IsProcessing)
            _statusText.Text = "●  No turn is running. Messages wait for the next turn.";
        else if (_runtime.ActiveSubAgentCount > 0)
            _statusText.Text = hold
                ? "🤖  A Sub Agent is running. It will wait before its next round, and the main AI waits after it."
                : "🤖  A Sub Agent is running. Messages go to the main AI after it reports back.";
        else if (hold)
            _statusText.Text = "⏳  The AI is finishing its current round, then it will wait.";
        else
            _statusText.Text = "▶  The AI is running. Messages are delivered at the next round start.";
    }

    // ── Queue rendering ───────────────────────────────────────────────────

    private void OnQueueChangedFromAnyThread()
    {
        if (Dispatcher.UIThread.CheckAccess()) RefreshQueue();
        else Dispatcher.UIThread.Post(RefreshQueue);
    }

    private void RefreshQueue()
    {
        List<PendingInjection> items = _runtime.SnapshotPending();

        // The message we were editing was already taken by the AI (or removed from elsewhere).
        if (_editingId != null && items.All(i => i.Id != _editingId))
        {
            _editingId = null;
            UpdateComposerMode();
            ShowNote("The AI already received the message you were editing. Saving now adds it as a NEW message.");
        }

        _queueList.Children.Clear();
        for (int i = 0; i < items.Count; i++)
            _queueList.Children.Add(BuildCard(items[i], i, items.Count));

        _emptyState.IsVisible  = items.Count == 0;
        _clearAllBtn.IsVisible = items.Count > 1;
        _queueHeader.Text      = items.Count == 0
            ? "Waiting for the next round"
            : $"Waiting for the next round  ·  {items.Count}";

        _onQueueChanged();
    }

    private Control BuildCard(PendingInjection p, int index, int total)
    {
        bool beingEdited = p.Id == _editingId;

        var badge = new Border
        {
            Background        = LlmChatWindow.ColAccent,
            CornerRadius      = new CornerRadius(8),
            Padding           = new Thickness(7, 1, 7, 1),
            VerticalAlignment = VerticalAlignment.Top,
            Margin            = new Thickness(0, 1, 10, 0),
            Child             = new TextBlock
            {
                Text       = "#" + (index + 1),
                FontSize   = 11,
                FontWeight = FontWeight.SemiBold,
                Foreground = Brushes.White,
            },
        };

        bool hasText = !string.IsNullOrWhiteSpace(p.Text);
        var text = new TextBlock
        {
            Text         = hasText ? p.Text.Trim() : "(no text. Only attachments / tools.)",
            FontSize     = 12.5,
            Foreground   = hasText ? LlmChatWindow.ColText : LlmChatWindow.ColMuted,
            FontStyle    = hasText ? FontStyle.Normal : FontStyle.Italic,
            TextWrapping = TextWrapping.Wrap,
            MaxLines     = 6,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var chips = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (string f in p.Files)
            chips.Children.Add(MakeChip("📄 " + Path.GetFileName(f), f));
        if (p.ToolNamesToLoad.Count > 0)
            chips.Children.Add(MakeChip("🧩 " + p.ToolNamesToLoad.Count + " tool(s)", string.Join(", ", p.ToolNamesToLoad)));
        chips.Children.Add(MakeChip("🕒 " + p.CreatedAt.ToString("HH:mm:ss"), "Queued at this time"));

        var textCol = new StackPanel();
        textCol.Children.Add(text);
        textCol.Children.Add(chips);

        var upBtn   = IconButton("▲", "Move up (delivered earlier)",  () => _runtime.MoveInjection(p.Id, -1));
        var downBtn = IconButton("▼", "Move down (delivered later)",  () => _runtime.MoveInjection(p.Id, +1));
        var editBtn = IconButton("✎", "Edit this message",            () => BeginEdit(p.Id));
        var delBtn  = IconButton("🗑", "Remove from the queue",        () => RemoveOne(p.Id), ColDanger);
        upBtn.IsEnabled   = index > 0;
        downBtn.IsEnabled = index < total - 1;

        var buttons = new StackPanel
        {
            Orientation       = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Top,
            Margin            = new Thickness(8, 0, 0, 0),
            Children          = { upBtn, downBtn, editBtn, delBtn },
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        Grid.SetColumn(badge,   0);
        Grid.SetColumn(textCol, 1);
        Grid.SetColumn(buttons, 2);
        grid.Children.Add(badge);
        grid.Children.Add(textCol);
        grid.Children.Add(buttons);

        var card = new Border
        {
            Background      = LlmChatWindow.BgAiBubble,
            BorderBrush     = beingEdited ? LlmChatWindow.ColAccent : LlmChatWindow.ColDivider,
            BorderThickness = new Thickness(beingEdited ? 2 : 1),
            CornerRadius    = new CornerRadius(10),
            Padding         = new Thickness(12, 10, 8, 10),
            Child           = grid,
        };

        // Double click = edit.
        card.DoubleTapped += (_, _) => BeginEdit(p.Id);
        return card;
    }

    private static Border MakeChip(string text, string? tip = null)
    {
        var chip = new Border
        {
            Background      = LlmChatWindow.BgThinking,
            BorderBrush     = LlmChatWindow.ColDivider,
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(8),
            Padding         = new Thickness(7, 1, 7, 1),
            Margin          = new Thickness(0, 0, 6, 4),
            Child           = new TextBlock
            {
                Text         = text,
                FontSize     = 10.5,
                Foreground   = LlmChatWindow.ColMuted,
                MaxWidth     = 260,
                TextTrimming = TextTrimming.CharacterEllipsis,
            },
        };
        if (!string.IsNullOrEmpty(tip)) ToolTip.SetTip(chip, tip);
        return chip;
    }

    private static Button IconButton(string glyph, string tip, Action onClick, IBrush? fg = null)
    {
        var b = new Button
        {
            Content         = glyph,
            FontSize        = 13,
            Padding         = new Thickness(7, 3, 7, 3),
            MinWidth        = 0,
            MinHeight       = 0,
            BorderThickness = new Thickness(0),
            CornerRadius    = new CornerRadius(6),
        };
        UiTheme.StyleLinkButton(b, fg ?? LlmChatWindow.ColMuted);
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => onClick();
        return b;
    }

    // ── Remove / edit ─────────────────────────────────────────────────────

    private void RemoveOne(string id)
    {
        bool wasEditing = _editingId == id;
        if (!_runtime.RemoveInjection(id))
            ShowNote("That message was already delivered to the AI. Nothing to remove.");
        if (wasEditing) ExitEditMode(clearComposer: true, note: null);
    }

    private void BeginEdit(string id)
    {
        PendingInjection? item = _runtime.SnapshotPending().FirstOrDefault(p => p.Id == id);
        if (item == null)
        {
            ShowNote("That message was already delivered to the AI. It can not be edited any more.");
            return;
        }

        _editingId = id;
        _textBox.Text = item.Text;

        _attachedFiles.Clear();
        _attachedFiles.AddRange(item.Files);
        RebuildFileChips();

        SetToolSelection(item.ToolNamesToLoad);

        HideNote();
        UpdateComposerMode();
        RefreshQueue();      // highlight the card being edited
        _textBox.Focus();
        _textBox.CaretIndex = _textBox.Text?.Length ?? 0;
    }

    private void ExitEditMode(bool clearComposer, string? note)
    {
        _editingId = null;
        if (clearComposer) ClearComposer();
        UpdateComposerMode();
        if (note != null) ShowNote(note); else HideNote();
        RefreshQueue();
    }

    private void UpdateComposerMode()
    {
        if (_editingId == null)
        {
            _composerTitle.Text     = "➕  New message";
            _saveBtn.Content        = "➕  Add to queue";
            _cancelEditBtn.IsVisible = false;
        }
        else
        {
            int idx = _runtime.SnapshotPending().FindIndex(p => p.Id == _editingId);
            _composerTitle.Text     = idx >= 0 ? $"✎  Editing message #{idx + 1}" : "✎  Editing message";
            _saveBtn.Content        = "💾  Save changes";
            _cancelEditBtn.IsVisible = true;
        }
    }

    private void ShowNote(string text)
    {
        _noteText.Text      = text;
        _noteText.IsVisible = true;
    }

    private void HideNote() => _noteText.IsVisible = false;

    // ── Composer: save ────────────────────────────────────────────────────

    private void SaveComposer()
    {
        string text = (_textBox.Text ?? string.Empty).Trim();

        if (text.Length == 0 && _attachedFiles.Count == 0 && _selectedTools.Count == 0)
        {
            ShowNote("Nothing to add. Type a message, attach a file, or pick a tool.");
            return;
        }

        HideNote();

        if (_editingId != null)
        {
            if (_runtime.UpdateInjection(_editingId, text, _attachedFiles, _selectedTools))
            {
                _editingId = null;
                ClearComposer();
                UpdateComposerMode();
                RefreshQueue();
                return;
            }

            // The AI took it while we were editing: keep the user's work as a NEW message.
            _editingId = null;
        }

        _runtime.EnqueueInjection(new PendingInjection
        {
            Text            = text,
            Files           = new List<string>(_attachedFiles),
            ToolNamesToLoad = _selectedTools.ToList(),
        });

        ClearComposer();
        UpdateComposerMode();
        _textBox.Focus();
    }

    private void ClearComposer()
    {
        _textBox.Text = string.Empty;
        _attachedFiles.Clear();
        RebuildFileChips();
        SetToolSelection(Array.Empty<string>());
    }

    // ── Tools ─────────────────────────────────────────────────────────────

    // The tool list UI itself is the shared ToolPreloadPopupWindow (universal picker + "My presets").

    /// <summary>Opens the shared tool picker on _selectedTools (the same HashSet, edited live).</summary>
    private async Task OpenToolPickerAsync()
    {
        var popup = new ToolPreloadPopupWindow(_selectedTools, UpdateToolsHeader, "Injection message");
        await popup.ShowDialog(this);
        UpdateToolsHeader();
    }

    /// <summary>Sets the tool selection to exactly names (also names that are not in the catalog any more).</summary>
    private void SetToolSelection(IEnumerable<string> names)
    {
        _selectedTools.Clear();
        foreach (string n in names) _selectedTools.Add(n);
        UpdateToolsHeader();
    }

    private void UpdateToolsHeader()
    {
        _toolsHeader.Text = _selectedTools.Count == 0
            ? "🧩  Load extra tools for the AI  (none)  —  click to choose or use a preset"
            : $"🧩  Load extra tools for the AI  ({_selectedTools.Count} selected)  —  click to change";
    }

    // ── File attachment ───────────────────────────────────────────────────

    private async Task PickFileAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider == null) return;

        var picked = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title         = "Attach File to Injection",
            AllowMultiple = true,
        });

        foreach (var file in picked)
        {
            string localPath = file.Path.LocalPath;
            if (_attachedFiles.Contains(localPath)) continue;
            _attachedFiles.Add(localPath);
        }
        RebuildFileChips();
    }

    private void RebuildFileChips()
    {
        _filesPanel.Children.Clear();
        foreach (string f in _attachedFiles.ToList())
            _filesPanel.Children.Add(BuildFileChip(f));
    }

    private Control BuildFileChip(string filePath)
    {
        var removeBtn = new Button
        {
            Content         = "×",
            FontSize        = 13,
            Padding         = new Thickness(4, 0, 4, 0),
            MinHeight       = 0,
            BorderThickness = new Thickness(0),
        };
        UiTheme.StyleLinkButton(removeBtn, LlmChatWindow.ColMuted);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(new TextBlock { Text = "📄", FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock
        {
            Text              = Path.GetFileName(filePath),
            FontSize          = 12,
            Foreground        = LlmChatWindow.ColText,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth          = 300,
            TextTrimming      = TextTrimming.CharacterEllipsis,
        });
        row.Children.Add(removeBtn);

        var chip = new Border
        {
            Background   = LlmChatWindow.BgAiBubble,
            CornerRadius = new CornerRadius(8),
            Padding      = new Thickness(8, 3, 4, 3),
            Margin       = new Thickness(0, 0, 6, 4),
            Child        = row,
        };
        ToolTip.SetTip(chip, filePath);

        removeBtn.Click += (_, _) =>
        {
            _attachedFiles.Remove(filePath);
            _filesPanel.Children.Remove(chip);
        };

        return chip;
    }

    /// <summary>Folder pasted clipboard images are saved to for THIS window.</summary>
    private static string PastedImagesDir =>
        Path.Combine(AppContext.BaseDirectory, "sessions", "_unsaved", "pasted");

    /// <summary>Ctrl+V handler: pastes a clipboard image as an attachment, else plain text.</summary>
    private async Task HandleClipboardPasteAsync()
    {
        IClipboard? clipboard = null;
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            clipboard = topLevel?.Clipboard;
            if (clipboard == null) return;

            string? imagePath = await ClipboardImageHelper.TryExtractClipboardImageAsync(clipboard, PastedImagesDir);
            if (imagePath != null)
            {
                if (!_attachedFiles.Contains(imagePath))
                {
                    _attachedFiles.Add(imagePath);
                    RebuildFileChips();
                }
            }
            else
            {
                await PasteTextFallbackAsync(clipboard);
            }
        }
        catch
        {
            try { if (clipboard != null) await PasteTextFallbackAsync(clipboard); }
            catch { /* ignore */ }
        }
    }

    /// <summary>Inserts the clipboard's plain text at the caret, replacing any selection.</summary>
    private async Task PasteTextFallbackAsync(IClipboard clipboard)
    {
        string? text = await clipboard.GetTextAsync();
        if (string.IsNullOrEmpty(text)) return;

        string current = _textBox.Text ?? string.Empty;
        int start = Math.Clamp(Math.Min(_textBox.SelectionStart, _textBox.SelectionEnd), 0, current.Length);
        int end   = Math.Clamp(Math.Max(_textBox.SelectionStart, _textBox.SelectionEnd), 0, current.Length);

        _textBox.Text = current[..start] + text + current[end..];
        _textBox.CaretIndex = start + text.Length;
    }
}
