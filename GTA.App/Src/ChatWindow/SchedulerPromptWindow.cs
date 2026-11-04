using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace GTA.App;

public sealed class SchedulerPromptWindow : Window
{
    private readonly LlmChatWindow _owner;

    private readonly TextBox    _promptBox;
    private readonly DatePicker _datePicker;
    private readonly TimePicker _timePicker;
    private readonly TextBlock  _errorLabel;

    // ── Attachments (📎) ──────────────────────────────────────────────────
    private readonly List<string> _attachments = new();
    private readonly StackPanel    _attachmentChipsPanel;
    private readonly Border        _attachmentStripBorder;
    private          Button?       _attachBtn;

    private readonly HashSet<string> _selectedTools = new(StringComparer.OrdinalIgnoreCase);
    private          Button?         _toolsBtn;

    public SchedulerPromptWindow(LlmChatWindow owner)
    {
        _owner = owner;

        Title                 = "⏰ Schedule a Prompt";
        Width                 = 480;
        Height                = 560;
        MinWidth              = 380;
        MinHeight             = 480;
        Background            = LlmChatWindow.BgMain;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize             = true;

        // ── Header ─────────────────────────────────────────────────────────
        var titleBlock = new TextBlock
        {
            Text       = "Schedule a Prompt",
            FontSize   = 18,
            FontWeight = FontWeight.SemiBold,
            Foreground = LlmChatWindow.ColText,
        };

        var subtitle = new TextBlock
        {
            Text         = "This will create a new chat marked \"⏳ WAITING\" and send the prompt " +
                           "below automatically once the chosen date/time is reached — or immediately, " +
                           "any time, via its own \"▶ Start Now\" button.",
            FontSize     = 11,
            Foreground   = LlmChatWindow.ColMuted,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 6, 0, 0),
        };

        var headerPanel = new StackPanel { Margin = new Thickness(20, 18, 20, 6) };
        headerPanel.Children.Add(titleBlock);
        headerPanel.Children.Add(subtitle);
        DockPanel.SetDock(headerPanel, Dock.Top);

        // ── Prompt textbox ─────────────────────────────────────────────────
        var promptLabel = new TextBlock
        {
            Text       = "Prompt",
            FontSize   = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = LlmChatWindow.ColMuted,
            Margin     = new Thickness(20, 10, 20, 4),
        };
        DockPanel.SetDock(promptLabel, Dock.Top);

        _promptBox = new TextBox
        {
            Watermark     = "What should the assistant do when this fires?",
            AcceptsReturn = true,
            TextWrapping  = TextWrapping.Wrap,
            Height        = 120,
            FontSize      = 13,
            Padding       = new Thickness(12, 10, 12, 10),
            Margin        = new Thickness(20, 0, 20, 8),
        };
        UiTheme.StyleTextBox(_promptBox, LlmChatWindow.BgAiBubble, LlmChatWindow.ColText,
            borderBrush: LlmChatWindow.ColDivider, focusBorderBrush: LlmChatWindow.ColAccent);
        DockPanel.SetDock(_promptBox, Dock.Top);

        // ── Attach files (📎) / Pre-load tools (🧩) buttons ──────────────────
        _attachBtn = new Button
        {
            Content  = "📎  Attach",
            FontSize = 12,
            Padding  = new Thickness(12, 6, 12, 6),
        };
        UiTheme.StyleButton(_attachBtn, LlmChatWindow.BgAiBubble, LlmChatWindow.ColText,
            hoverBackground: LlmChatWindow.BgHoverItem, pressedBackground: LlmChatWindow.BgActiveItem,
            borderBrush: LlmChatWindow.ColDivider);
        ToolTip.SetTip(_attachBtn, "Attach one or more image/text files to send along with this prompt when it fires");
        _attachBtn.Click += async (_, _) => await OpenFilePickerAsync();

        _toolsBtn = new Button
        {
            Content  = "🧩  Preload Tools",
            FontSize = 12,
            Padding  = new Thickness(12, 6, 12, 6),
        };
        UiTheme.StyleButton(_toolsBtn, LlmChatWindow.BgAiBubble, LlmChatWindow.ColText,
            hoverBackground: LlmChatWindow.BgHoverItem, pressedBackground: LlmChatWindow.BgActiveItem,
            borderBrush: LlmChatWindow.ColDivider);
        ToolTip.SetTip(_toolsBtn, "Pick specific tools to have available the moment this prompt fires");
        _toolsBtn.Click += async (_, _) => await OpenPreloadToolsPopupAsync();

        var actionRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing     = 8,
            Margin      = new Thickness(20, 0, 20, 6),
        };
        actionRow.Children.Add(_attachBtn);
        actionRow.Children.Add(_toolsBtn);
        DockPanel.SetDock(actionRow, Dock.Top);

        // Attachment chip strip — hidden until at least one file is attached.
        _attachmentChipsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing     = 6,
        };
        _attachmentStripBorder = new Border
        {
            IsVisible    = false,
            Background   = LlmChatWindow.BgAiBubble,
            CornerRadius = new CornerRadius(8),
            Padding      = new Thickness(8, 6, 8, 6),
            Margin       = new Thickness(20, 0, 20, 8),
            Child        = new ScrollViewer
            {
                Content = _attachmentChipsPanel,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility   = ScrollBarVisibility.Disabled,
                MaxHeight = 44,
            },
        };
        DockPanel.SetDock(_attachmentStripBorder, Dock.Top);

        // ── Date / Time pickers ────────────────────────────────────────────
        var whenLabel = new TextBlock
        {
            Text       = "Date & Time",
            FontSize   = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = LlmChatWindow.ColMuted,
            Margin     = new Thickness(20, 4, 20, 4),
        };
        DockPanel.SetDock(whenLabel, Dock.Top);

        var now = DateTimeOffset.Now;

        _datePicker = new DatePicker
        {
            SelectedDate = now.Date,
            FontSize     = 13,
        };
        _timePicker = new TimePicker
        {
            SelectedTime = now.TimeOfDay.Add(TimeSpan.FromMinutes(5)),
            FontSize     = 13,
        };

        var pickerRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing     = 10,
            Margin      = new Thickness(20, 0, 20, 6),
        };
        pickerRow.Children.Add(_datePicker);
        pickerRow.Children.Add(_timePicker);
        DockPanel.SetDock(pickerRow, Dock.Top);

        // Convenience "quick pick" chips.
        var quickRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing     = 6,
            Margin      = new Thickness(20, 0, 20, 10),
        };
        foreach (var (label, delta) in new (string, TimeSpan)[]
                 {
                     ("+5m",  TimeSpan.FromMinutes(5)),
                     ("+15m", TimeSpan.FromMinutes(15)),
                     ("+1h",  TimeSpan.FromHours(1)),
                     ("+1d",  TimeSpan.FromDays(1)),
                 })
        {
            var chip = new Button
            {
                Content  = label,
                FontSize = 11,
                Padding  = new Thickness(10, 4, 10, 4),
            };
            UiTheme.StyleButton(chip, LlmChatWindow.BgAiBubble, LlmChatWindow.ColText,
                hoverBackground: LlmChatWindow.BgHoverItem, pressedBackground: LlmChatWindow.BgActiveItem,
                borderBrush: LlmChatWindow.ColDivider);
            chip.Click += (_, _) =>
            {
                var target = DateTimeOffset.Now.Add(delta);
                _datePicker.SelectedDate = target.Date;
                _timePicker.SelectedTime = target.TimeOfDay;
            };
            quickRow.Children.Add(chip);
        }
        DockPanel.SetDock(quickRow, Dock.Top);

        // ── Error label (validation) ───────────────────────────────────────
        _errorLabel = new TextBlock
        {
            Text         = string.Empty,
            FontSize     = 11,
            Foreground   = new SolidColorBrush(Color.FromRgb(230, 120, 120)),
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(20, 0, 20, 4),
            IsVisible    = false,
        };
        DockPanel.SetDock(_errorLabel, Dock.Top);

        // ── Footer buttons ─────────────────────────────────────────────────
        var cancelBtn = new Button
        {
            Content = "Cancel",
            FontSize = 12,
            Padding  = new Thickness(16, 7, 16, 7),
        };
        UiTheme.StyleButton(cancelBtn, LlmChatWindow.ColDisabled, LlmChatWindow.ColText);
        cancelBtn.Click += (_, _) => Close();

        var scheduleBtn = new Button
        {
            Content    = "⏰  Schedule",
            FontSize   = 13,
            FontWeight = FontWeight.SemiBold,
            Padding    = new Thickness(18, 7, 18, 7),
        };
        UiTheme.StyleButton(scheduleBtn, LlmChatWindow.ColScheduledBadgeBg, Brushes.White);
        scheduleBtn.Click += (_, _) => TryConfirmSchedule();

        var footer = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing             = 8,
            Margin              = new Thickness(20, 8, 20, 16),
        };
        footer.Children.Add(cancelBtn);
        footer.Children.Add(scheduleBtn);
        DockPanel.SetDock(footer, Dock.Bottom);

        var root = new DockPanel { LastChildFill = true };
        root.Children.Add(headerPanel);
        root.Children.Add(footer);
        root.Children.Add(promptLabel);
        root.Children.Add(_promptBox);
        root.Children.Add(actionRow);
        root.Children.Add(_attachmentStripBorder);
        root.Children.Add(whenLabel);
        root.Children.Add(pickerRow);
        root.Children.Add(quickRow);
        root.Children.Add(_errorLabel);

        Content = root;
    }

    // ── Attachments ────────────────────────────────────────────────────────

    /// <summary>Opens the file picker to attach image/text files.</summary>
    private async Task OpenFilePickerAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider == null) return;

        var picked = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title          = "Attach File",
            AllowMultiple  = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Images")
                {
                    Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp" }
                },
                new FilePickerFileType("Text files")
                {
                    Patterns = new[]
                    {
                        "*.txt", "*.md", "*.log", "*.json", "*.xml", "*.csv",
                        "*.cs", "*.py", "*.js", "*.ts", "*.html", "*.css",
                        "*.yaml", "*.yml", "*.ini", "*.cfg",
                    }
                },
                new FilePickerFileType("All files")
                {
                    Patterns = new[] { "*.*" }
                },
            }
        });

        foreach (var file in picked)
        {
            string localPath = file.Path.LocalPath;
            if (!_attachments.Contains(localPath))
                _attachments.Add(localPath);
        }

        RefreshAttachmentStrip();
    }

    private void RemoveAttachment(string path)
    {
        _attachments.Remove(path);
        RefreshAttachmentStrip();
    }

    private void RefreshAttachmentStrip()
    {
        _attachmentChipsPanel.Children.Clear();
        foreach (var path in _attachments)
            _attachmentChipsPanel.Children.Add(BuildAttachmentChip(path));

        _attachmentStripBorder.IsVisible = _attachments.Count > 0;

        if (_attachBtn != null)
            _attachBtn.Content = _attachments.Count > 0
                ? $"📎  Attach ({_attachments.Count})"
                : "📎  Attach";
    }

    private Control BuildAttachmentChip(string filePath)
    {
        var row = new StackPanel
        {
            Orientation       = Orientation.Horizontal,
            Spacing           = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };

        row.Children.Add(new TextBlock
        {
            Text              = "📄",
            FontSize          = 14,
            VerticalAlignment = VerticalAlignment.Center,
        });

        row.Children.Add(new TextBlock
        {
            Text              = Path.GetFileName(filePath),
            FontSize          = 11,
            Foreground        = LlmChatWindow.ColText,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth          = 120,
            TextTrimming      = TextTrimming.CharacterEllipsis,
        });

        var removeBtn = new Button
        {
            Content           = "×",
            FontSize          = 13,
            Padding           = new Thickness(4, 0, 4, 0),
            BorderThickness   = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        UiTheme.StyleLinkButton(removeBtn, LlmChatWindow.ColMuted);
        string capturedPath = filePath;
        removeBtn.Click += (_, _) => RemoveAttachment(capturedPath);
        row.Children.Add(removeBtn);

        return new Border
        {
            Background   = LlmChatWindow.BgCodeBlock,
            CornerRadius = new CornerRadius(6),
            Padding      = new Thickness(8, 4, 8, 4),
            Child        = row,
        };
    }

    // ── Pre-load tools ─────────────────────────────────────────────────────

    /// <summary>Opens the "Pre-load Tools" popup for this window's own tool set.</summary>
    private async Task OpenPreloadToolsPopupAsync()
    {
        var popup = new ToolPreloadPopupWindow(_selectedTools, RefreshToolsButtonLabel, "Scheduled Prompt");
        await popup.ShowDialog(this);
    }

    private void RefreshToolsButtonLabel()
    {
        if (_toolsBtn == null) return;
        _toolsBtn.Content = _selectedTools.Count > 0
            ? $"🧩  Preload Tools ({_selectedTools.Count})"
            : "🧩  Preload Tools";
    }

    // ── Confirm ────────────────────────────────────────────────────────────

    private void TryConfirmSchedule()
    {
        string prompt = _promptBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(prompt))
        {
            ShowError("Please type a prompt to schedule.");
            return;
        }

        DateOnly? date = _datePicker.SelectedDate.HasValue
            ? DateOnly.FromDateTime(_datePicker.SelectedDate.Value.DateTime)
            : null;
        TimeSpan? time = _timePicker.SelectedTime;

        if (date == null || time == null)
        {
            ShowError("Please pick both a date and a time.");
            return;
        }

        var when = new DateTimeOffset(date.Value.ToDateTime(TimeOnly.FromTimeSpan(time.Value)));

        if (when <= DateTimeOffset.Now)
        {
            ShowError("Please pick a date/time in the future.");
            return;
        }

        _owner.CreateScheduledChat(prompt, when, _attachments, _selectedTools);
        Close();
    }

    private void ShowError(string message)
    {
        _errorLabel.Text      = message;
        _errorLabel.IsVisible = true;
    }
}
