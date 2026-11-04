using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace GTA.App;

public sealed class RunningTaskPickerWindow : Window
{
    /// <summary>The user's confirmed pick, or null if cancelled/closed.</summary>
    public ChatSession? Result { get; private set; } = null;

    private readonly LlmChatWindow _owner;
    private readonly string?       _excludeSessionId;

    private List<ChatSession>  _candidates = new();
    private readonly List<Border> _rowBorders = new();
    private int _selectedIndex = -1;

    private readonly StackPanel _listPanel;
    private readonly TextBlock  _emptyLabel;
    private readonly TextBlock  _errorLabel;
    private readonly Button     _pickBtn;

    public RunningTaskPickerWindow(LlmChatWindow owner, string? excludeSessionId = null)
    {
        _owner            = owner;
        _excludeSessionId = excludeSessionId;

        Title                 = "⛓ Pick a Running Task";
        Width                 = 460;
        Height                = 480;
        MinWidth              = 380;
        MinHeight             = 360;
        Background            = LlmChatWindow.BgMain;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize             = true;

        // ── Header ─────────────────────────────────────────────────────────
        var titleBlock = new TextBlock
        {
            Text       = "Pick a Running Task",
            FontSize   = 16,
            FontWeight = FontWeight.SemiBold,
            Foreground = LlmChatWindow.ColText,
        };

        var subtitle = new TextBlock
        {
            Text = "This step pauses the blueprint until the task you pick below stops " +
                   "running (finishes, fails, or is cancelled), then continues to the next step.",
            FontSize     = 11,
            Foreground   = LlmChatWindow.ColMuted,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 4, 0, 0),
        };

        var headerPanel = new StackPanel { Margin = new Thickness(20, 18, 20, 6) };
        headerPanel.Children.Add(titleBlock);
        headerPanel.Children.Add(subtitle);
        DockPanel.SetDock(headerPanel, Dock.Top);

        // ── Refresh ────────────────────────────────────────────────────────
        var refreshBtn = new Button
        {
            Content = "🔄  Refresh List", FontSize = 12, Padding = new Thickness(12, 6, 12, 6),
        };
        UiTheme.StyleButton(refreshBtn, LlmChatWindow.BgAiBubble, LlmChatWindow.ColText,
            hoverBackground: LlmChatWindow.BgHoverItem, pressedBackground: LlmChatWindow.BgActiveItem,
            borderBrush: LlmChatWindow.ColDivider);
        ToolTip.SetTip(refreshBtn, "Re-scan for currently-running tasks");
        refreshBtn.Click += (_, _) => RefreshCandidates();

        var refreshRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, Margin = new Thickness(20, 0, 20, 6),
        };
        refreshRow.Children.Add(refreshBtn);
        DockPanel.SetDock(refreshRow, Dock.Top);

        // ── Error label (validation / stale-pick recheck) ──────────────────
        _errorLabel = new TextBlock
        {
            Text         = string.Empty,
            FontSize     = 11,
            Foreground   = new SolidColorBrush(Color.FromRgb(230, 120, 120)),
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(20, 0, 20, 4),
            IsVisible    = false,
        };
        DockPanel.SetDock(_errorLabel, Dock.Bottom);

        // ── Footer ────────────────────────────────────────────────────────
        var cancelBtn = new Button { Content = "Cancel", FontSize = 12, Padding = new Thickness(16, 7, 16, 7) };
        UiTheme.StyleButton(cancelBtn, LlmChatWindow.ColDisabled, LlmChatWindow.ColText);
        cancelBtn.Click += (_, _) => Close();

        _pickBtn = new Button
        {
            Content    = "⛓  Chain Selected",
            FontSize   = 13,
            FontWeight = FontWeight.SemiBold,
            Padding    = new Thickness(18, 7, 18, 7),
            IsEnabled  = false,
        };
        UiTheme.StyleButton(_pickBtn, LlmChatWindow.ColTaskRunningBadgeBg, LlmChatWindow.ColTaskRunningBadgeFg,
            disabledBackground: LlmChatWindow.ColDisabled, disabledForeground: LlmChatWindow.ColMuted);
        _pickBtn.Click += (_, _) => TryConfirm();

        var footer = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing             = 8,
            Margin              = new Thickness(20, 8, 20, 16),
        };
        footer.Children.Add(cancelBtn);
        footer.Children.Add(_pickBtn);
        DockPanel.SetDock(footer, Dock.Bottom);

        // ── "No task running" empty-state message ──────────────────────────
        _emptyLabel = new TextBlock
        {
            Text = "⚠  No task is currently running right now.\n\n" +
                   "Start one first (📋 Task → ▶ Run Task), then come back and add this step " +
                   "to chain onto it.",
            FontSize     = 12,
            Foreground   = LlmChatWindow.ColMuted,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(20, 20, 20, 0),
            IsVisible    = false,
        };
        DockPanel.SetDock(_emptyLabel, Dock.Top);

        // ── Scrollable candidate list (the "fill" element) ────────────────
        _listPanel = new StackPanel { Spacing = 4, Margin = new Thickness(20, 0, 20, 8) };
        var listScroll = new ScrollViewer
        {
            Content                       = _listPanel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
        };

        var root = new DockPanel { LastChildFill = true };
        root.Children.Add(headerPanel);
        root.Children.Add(refreshRow);
        root.Children.Add(_emptyLabel);
        root.Children.Add(footer);
        root.Children.Add(_errorLabel);
        root.Children.Add(listScroll); // added LAST → fills remaining space

        Content = root;

        RefreshCandidates();
    }

    // ── Candidate list ─────────────────────────────────────────────────────

    private void RefreshCandidates()
    {
        _errorLabel.IsVisible = false;
        _selectedIndex        = -1;

        _candidates = _owner.GetCurrentlyRunningTaskSessions(_excludeSessionId);

        _listPanel.Children.Clear();
        _rowBorders.Clear();

        bool anyRunning = _candidates.Count > 0;
        _emptyLabel.IsVisible = !anyRunning;
        _pickBtn.IsEnabled    = false; // re-enabled once a row is actually selected below

        for (int i = 0; i < _candidates.Count; i++)
        {
            int capturedIndex = i;
            var session = _candidates[i];

            var titleLabel = new TextBlock
            {
                Text              = session.Title,
                FontSize          = 13,
                Foreground        = LlmChatWindow.ColText,
                TextWrapping      = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };

            int    current   = session.Runtime.TaskCurrentUnitIndex;
            int    total     = session.TaskUnits.Count;
            string stepInfo  = total > 0 ? $"Step {current + 1}/{total}" : "Running";

            var subLabel = new TextBlock
            {
                Text       = $"📋  {stepInfo}",
                FontSize   = 11,
                Foreground = LlmChatWindow.ColMuted,
            };

            var textCol = new StackPanel { Spacing = 2 };
            textCol.Children.Add(titleLabel);
            textCol.Children.Add(subLabel);

            var border = new Border
            {
                CornerRadius    = new CornerRadius(6),
                Padding         = new Thickness(10, 8, 10, 8),
                Margin          = new Thickness(0, 1, 0, 1),
                Cursor          = new Cursor(StandardCursorType.Hand),
                Background      = Brushes.Transparent,
                BorderBrush     = LlmChatWindow.ColDivider,
                BorderThickness = new Thickness(1),
                Child           = textCol,
                Tag             = capturedIndex,
            };

            border.PointerEntered += (_, _) =>
            {
                if (_selectedIndex != capturedIndex) border.Background = LlmChatWindow.BgHoverItem;
            };
            border.PointerExited += (_, _) =>
            {
                if (_selectedIndex != capturedIndex) border.Background = Brushes.Transparent;
            };
            border.PointerPressed += (_, _) => SelectRow(capturedIndex);

            _listPanel.Children.Add(border);
            _rowBorders.Add(border);
        }
    }

    private void SelectRow(int index)
    {
        _selectedIndex        = index;
        _errorLabel.IsVisible = false;

        for (int i = 0; i < _rowBorders.Count; i++)
            _rowBorders[i].Background = i == index ? LlmChatWindow.BgActiveItem : Brushes.Transparent;

        _pickBtn.IsEnabled = true;
    }

    // ── Confirm ────────────────────────────────────────────────────────────

    private void TryConfirm()
    {
        if (_selectedIndex < 0 || _selectedIndex >= _candidates.Count)
        {
            ShowError("Please select a running task first.");
            return;
        }

        var picked = _candidates[_selectedIndex];

        if (!_owner.IsTaskSessionStillRunning(picked))
        {
            ShowError($"⚠  '{picked.Title}' is no longer running — it may have just finished. " +
                      "The list has been refreshed; please pick another, or Cancel.");
            RefreshCandidates();
            return;
        }

        Result = picked;
        Close();
    }

    private void ShowError(string message)
    {
        _errorLabel.Text      = message;
        _errorLabel.IsVisible = true;
    }
}
