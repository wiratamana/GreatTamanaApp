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
using Avalonia.Threading;
using Avalonia.Platform.Storage;

namespace GTA.App;

public sealed class TaskUnitEditWindow : Window
{
    /// <summary>Set when the user confirms: the edited existing unit, or a new one.</summary>
    public TaskUnit? Result { get; private set; } = null;

    private readonly TaskUnitKind _kind;
    private readonly TaskUnit?    _existing;
    private readonly TextBox?     _promptBox;
    private CalendarDatePicker? _datePicker;
    private TimePicker?         _timePicker;
    private TextBlock?          _previewLabel;
    private DispatcherTimer?    _clockTimer;
    private readonly TextBlock    _errorLabel;

    // ── Per-step attachments (📎) — Prompt kind only ──────────────────────
    private readonly List<string> _attachments = new();
    private StackPanel?           _attachmentChipsPanel;
    private Border?               _attachmentStripBorder;
    private Button?               _attachBtn;

    // ── Per-step pre-loaded tools (🧩) — Prompt kind only ─────────────────
    private readonly HashSet<string> _selectedTools = new(StringComparer.OrdinalIgnoreCase);
    private Button?                  _toolsBtn;

    public TaskUnitEditWindow(
        TaskUnitKind kind, TaskUnit? existing = null,
        IEnumerable<string>? globalAttachments = null, IEnumerable<string>? globalPreloadedTools = null)
    {
        _kind     = kind;
        _existing = existing;

        if (existing != null)
        {
            _attachments.AddRange(existing.Attachments);
            foreach (var name in existing.PreloadedToolNames)
                _selectedTools.Add(name);
        }
        else
        {
            if (globalAttachments != null)
                foreach (var path in globalAttachments)
                    if (!_attachments.Contains(path))
                        _attachments.Add(path);

            if (globalPreloadedTools != null)
                foreach (var name in globalPreloadedTools)
                    _selectedTools.Add(name);
        }

        Title                 = kind == TaskUnitKind.Prompt ? "💬 Prompt Step" : "⏱ Wait Step";
        Width                 = kind == TaskUnitKind.Prompt ? 460 : 560;
        Height                = kind == TaskUnitKind.Prompt ? 500 : 420;
        MinWidth              = kind == TaskUnitKind.Prompt ? 380 : 500;
        MinHeight             = kind == TaskUnitKind.Prompt ? 420 : 380;
        Background            = LlmChatWindow.BgMain;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize             = true;

        // ── Header ─────────────────────────────────────────────────────────
        var titleBlock = new TextBlock
        {
            Text       = kind == TaskUnitKind.Prompt ? "Create Prompt" : "Wait (delay)",
            FontSize   = 16,
            FontWeight = FontWeight.SemiBold,
            Foreground = LlmChatWindow.ColText,
        };

        var subtitle = new TextBlock
        {
            Text = kind == TaskUnitKind.Prompt
                ? "This exact text is sent to the LLM (and fully awaited, all agentic " +
                  "rounds) when this step runs — the blueprint only continues to the " +
                  "next step once it has completely finished."
                : "Pauses the task until the date & time you pick (local machine time). " +
                  "The timer starts when this step starts: target − current machine time. " +
                  "Past dates and times are not allowed.",
            FontSize     = 11,
            Foreground   = LlmChatWindow.ColMuted,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 4, 0, 0),
        };

        var headerPanel = new StackPanel { Margin = new Thickness(20, 18, 20, 6) };
        headerPanel.Children.Add(titleBlock);
        headerPanel.Children.Add(subtitle);
        DockPanel.SetDock(headerPanel, Dock.Top);

        // ── Error label (validation) ───────────────────────────────────────
        _errorLabel = new TextBlock
        {
            Text         = string.Empty,
            FontSize     = 11,
            Foreground   = new SolidColorBrush(Color.FromRgb(230, 120, 120)),
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(20, 4, 20, 4),
            IsVisible    = false,
        };
        DockPanel.SetDock(_errorLabel, Dock.Top);

        // ── Footer ────────────────────────────────────────────────────────
        var cancelBtn = new Button { Content = "Cancel", FontSize = 12, Padding = new Thickness(16, 7, 16, 7) };
        UiTheme.StyleButton(cancelBtn, LlmChatWindow.ColDisabled, LlmChatWindow.ColText);
        cancelBtn.Click += (_, _) => Close();

        var confirmBtn = new Button
        {
            Content    = existing != null ? "Save" : "Add",
            FontSize   = 13,
            FontWeight = FontWeight.SemiBold,
            Padding    = new Thickness(18, 7, 18, 7),
        };
        UiTheme.StyleButton(confirmBtn, LlmChatWindow.ColAccent, Brushes.White);
        confirmBtn.Click += (_, _) => TryConfirm();

        var footer = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing             = 8,
            Margin               = new Thickness(20, 8, 20, 16),
        };
        footer.Children.Add(cancelBtn);
        footer.Children.Add(confirmBtn);
        DockPanel.SetDock(footer, Dock.Bottom);

        var root = new DockPanel { LastChildFill = true };
        root.Children.Add(headerPanel);
        root.Children.Add(_errorLabel);
        root.Children.Add(footer);

        if (kind == TaskUnitKind.Prompt)
        {
            _promptBox = new TextBox
            {
                Watermark     = "What should the assistant do in this step?",
                AcceptsReturn = true,
                TextWrapping  = TextWrapping.Wrap,
                FontSize      = 13,
                Height        = 150,
                Padding       = new Thickness(12, 10, 12, 10),
                Margin        = new Thickness(20, 12, 20, 8),
                Text          = existing?.PromptText ?? string.Empty,
            };
            UiTheme.StyleTextBox(_promptBox, LlmChatWindow.BgAiBubble, LlmChatWindow.ColText,
                borderBrush: LlmChatWindow.ColDivider, focusBorderBrush: LlmChatWindow.ColAccent);
            DockPanel.SetDock(_promptBox, Dock.Top);
            root.Children.Add(_promptBox);

            _promptBox.AddHandler(
                InputElement.KeyDownEvent,
                (object? _, KeyEventArgs e) =>
                {
                    if (e.Key == Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Control))
                    {
                        e.Handled = true;
                        _ = HandleClipboardPasteAsync();
                    }
                },
                handledEventsToo: false,
                routes: RoutingStrategies.Tunnel);

            // ── "📎 Attach" / "🧩 Preload Tools" — THIS step's own selection ──
            _attachBtn = new Button
            {
                Content  = "📎  Attach",
                FontSize = 12,
                Padding  = new Thickness(12, 6, 12, 6),
            };
            UiTheme.StyleButton(_attachBtn, LlmChatWindow.BgAiBubble, LlmChatWindow.ColText,
                hoverBackground: LlmChatWindow.BgHoverItem, pressedBackground: LlmChatWindow.BgActiveItem,
                borderBrush: LlmChatWindow.ColDivider);
            ToolTip.SetTip(_attachBtn, "Attach one or more image/text files to send along with THIS step's prompt");
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
            ToolTip.SetTip(_toolsBtn, "Pick specific tools to have available ONLY when THIS step's prompt fires");
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
            root.Children.Add(actionRow);

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
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                },
            };
            root.Children.Add(_attachmentStripBorder); // fill element

            RefreshAttachmentStrip();
            RefreshToolsButtonLabel();
        }
        else
        {
            DateTime initial;
            if (existing?.WaitUntil is DateTime keep && keep > DateTime.Now)
                initial = keep;
            else if (existing != null && existing.WaitSeconds > 0)
                initial = DateTime.Now.AddSeconds(existing.WaitSeconds);
            else
                initial = DateTime.Now.AddMinutes(5);
            initial = TruncateToMinute(initial);
            if (initial <= DateTime.Now)
                initial = TruncateToMinute(DateTime.Now.AddMinutes(1));

            var pickLabel = new TextBlock
            {
                Text       = "Wait until (local machine time)",
                FontSize   = 12,
                FontWeight = FontWeight.SemiBold,
                Foreground = LlmChatWindow.ColMuted,
                Margin     = new Thickness(20, 12, 20, 4),
            };
            DockPanel.SetDock(pickLabel, Dock.Top);
            root.Children.Add(pickLabel);

            // Calendar: every day before today is greyed out / not selectable.
            _datePicker = new CalendarDatePicker
            {
                SelectedDate         = initial.Date,
                DisplayDateStart     = DateTime.Today,
                SelectedDateFormat   = CalendarDatePickerFormat.Custom,
                CustomDateFormatString = "ddd, yyyy-MM-dd",
                FontSize             = 13,
                HorizontalAlignment  = HorizontalAlignment.Stretch,
            };

            _timePicker = new TimePicker
            {
                SelectedTime        = new TimeSpan(initial.Hour, initial.Minute, 0),
                ClockIdentifier     = "24HourClock",
                MinuteIncrement     = 1,
                FontSize            = 13,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };

            var pickerGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,12,*"),
                Margin            = new Thickness(20, 0, 20, 8),
            };
            Grid.SetColumn(_datePicker, 0);
            Grid.SetColumn(_timePicker, 2);
            pickerGrid.Children.Add(_datePicker);
            pickerGrid.Children.Add(_timePicker);
            DockPanel.SetDock(pickerGrid, Dock.Top);
            root.Children.Add(pickerGrid);

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
                         ("+6h",  TimeSpan.FromHours(6)),
                         ("+1d",  TimeSpan.FromDays(1)),
                         ("+1w",  TimeSpan.FromDays(7)),
                     })
            {
                var chip = new Button { Content = label, FontSize = 11, Padding = new Thickness(10, 4, 10, 4) };
                UiTheme.StyleButton(chip, LlmChatWindow.BgAiBubble, LlmChatWindow.ColText,
                    hoverBackground: LlmChatWindow.BgHoverItem, pressedBackground: LlmChatWindow.BgActiveItem,
                    borderBrush: LlmChatWindow.ColDivider);
                var capturedDelta = delta;
                chip.Click += (_, _) =>
                {
                    // Quick-pick = "from now", always lands in the future.
                    var t = TruncateToMinute(DateTime.Now.Add(capturedDelta));
                    _datePicker!.SelectedDate = t.Date;
                    _timePicker!.SelectedTime = new TimeSpan(t.Hour, t.Minute, 0);
                    RefreshWaitPreview();
                };
                quickRow.Children.Add(chip);
            }
            DockPanel.SetDock(quickRow, Dock.Top);
            root.Children.Add(quickRow);

            // Live preview of the timer — added LAST → the DockPanel's "fill" element.
            _previewLabel = new TextBlock
            {
                FontSize     = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(20, 4, 20, 8),
            };
            root.Children.Add(_previewLabel);

            _datePicker.PropertyChanged += (_, e) =>
            {
                if (e.Property == CalendarDatePicker.SelectedDateProperty) RefreshWaitPreview();
            };
            _timePicker.PropertyChanged += (_, e) =>
            {
                if (e.Property == TimePicker.SelectedTimeProperty) RefreshWaitPreview();
            };

            // Keeps "now" / the remaining time live, and flags the pick red as
            // soon as the machine clock passes it.
            _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _clockTimer.Tick += (_, _) => RefreshWaitPreview();
            _clockTimer.Start();
            Closed += (_, _) => _clockTimer?.Stop();

            RefreshWaitPreview();
        }

        if (kind == TaskUnitKind.Prompt)
        {
            DragDrop.SetAllowDrop(root, true);
            root.AddHandler(DragDrop.DragOverEvent, OnWindowDragOver);
            root.AddHandler(DragDrop.DropEvent, OnWindowDrop);
        }

        Content = root;
    }

    // ── Attachments (📎) ──────────────────────────────────────────────────

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
        if (_attachmentChipsPanel == null || _attachmentStripBorder == null) return;

        _attachmentChipsPanel.Children.Clear();
        foreach (var path in _attachments)
            _attachmentChipsPanel.Children.Add(BuildAttachmentChip(path));

        _attachmentStripBorder.IsVisible = _attachments.Count > 0;

        if (_attachBtn != null)
            _attachBtn.Content = _attachments.Count > 0
                ? $"📎  Attach ({_attachments.Count})"
                : "📎  Attach";
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
                if (!_attachments.Contains(imagePath))
                    _attachments.Add(imagePath);
                RefreshAttachmentStrip();
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
        if (string.IsNullOrEmpty(text) || _promptBox == null) return;

        string current = _promptBox.Text ?? string.Empty;
        int start = Math.Clamp(Math.Min(_promptBox.SelectionStart, _promptBox.SelectionEnd), 0, current.Length);
        int end   = Math.Clamp(Math.Max(_promptBox.SelectionStart, _promptBox.SelectionEnd), 0, current.Length);

        _promptBox.Text = current[..start] + text + current[end..];
        _promptBox.CaretIndex = start + text.Length;
    }

    private void OnWindowDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files)
            ? DragDropEffects.Copy
            : DragDropEffects.None;

        e.Handled = true;
    }

    private void OnWindowDrop(object? sender, DragEventArgs e)
    {
        if (!e.Data.Contains(DataFormats.Files)) return;

        var items = e.Data.GetFiles();
        if (items == null) return;

        foreach (var item in items)
        {
            string? localPath = item.TryGetLocalPath();
            if (!string.IsNullOrEmpty(localPath) && File.Exists(localPath) && !_attachments.Contains(localPath))
                _attachments.Add(localPath);
        }

        RefreshAttachmentStrip();
        e.Handled = true;
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

    // ── Pre-load tools (🧩) ───────────────────────────────────────────────

    /// <summary>Opens the "Pre-load Tools" popup for this unit's own tool set.</summary>
    private async Task OpenPreloadToolsPopupAsync()
    {
        var popup = new ToolPreloadPopupWindow(_selectedTools, RefreshToolsButtonLabel, "Task Step");
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

    private void TryConfirm()
    {
        if (_kind == TaskUnitKind.Prompt)
        {
            string text = _promptBox!.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(text))
            {
                ShowError("Please type a prompt.");
                return;
            }

            var unit = _existing ?? new TaskUnit();
            unit.Kind               = TaskUnitKind.Prompt;
            unit.PromptText         = text;
            unit.Attachments        = new List<string>(_attachments);
            unit.PreloadedToolNames = _selectedTools.ToList();
            Result = unit;
        }
        else
        {
            var target = GetPickedTarget();
            if (target == null)
            {
                ShowError("Please pick both a date and a time.");
                return;
            }

            // Past date/time is illegal — judged against the local machine clock.
            if (target.Value <= DateTime.Now)
            {
                ShowError("That date/time is in the past. Please pick a future date and time (local machine time).");
                return;
            }

            var unit = _existing ?? new TaskUnit();
            unit.Kind        = TaskUnitKind.Wait;
            unit.WaitUntil   = target.Value;
            // Keep the legacy field in sync (rough duration from now) so anything
            // that only knows seconds still shows something sensible.
            unit.WaitSeconds = Math.Max(1, (target.Value - DateTime.Now).TotalSeconds);
            Result = unit;
        }

        _clockTimer?.Stop();
        Close();
    }

    // ── Wait step: calendar + time → target DateTime ──────────────────────

    /// <summary>Combines the picked date and time-of-day into one local DateTime, or null if either is missing.</summary>
    private DateTime? GetPickedTarget()
    {
        if (_datePicker?.SelectedDate is not DateTime date || _timePicker?.SelectedTime is not TimeSpan time)
            return null;

        return date.Date.AddHours(time.Hours).AddMinutes(time.Minutes);
    }

    /// <summary>Updates the live "timer will run for …" line under the pickers, using the CURRENT machine time.</summary>
    private void RefreshWaitPreview()
    {
        if (_previewLabel == null) return;

        var target = GetPickedTarget();
        if (target == null)
        {
            _previewLabel.Text       = "Pick a date and a time.";
            _previewLabel.Foreground = LlmChatWindow.ColMuted;
            return;
        }

        var now       = DateTime.Now;
        var remaining = target.Value - now;
        if (remaining <= TimeSpan.Zero)
        {
            _previewLabel.Text       = "⚠ This date/time is in the past. Pick a future one.";
            _previewLabel.Foreground = new SolidColorBrush(Color.FromRgb(230, 120, 120));
            return;
        }

        _previewLabel.Text =
            $"⏳ Timer: {FormatRemaining(remaining)}\n" +
            $"Now: {now:ddd yyyy-MM-dd HH:mm}   →   Until: {target.Value:ddd yyyy-MM-dd HH:mm}";
        _previewLabel.Foreground = LlmChatWindow.ColText;
    }

    private static DateTime TruncateToMinute(DateTime t) =>
        new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0, DateTimeKind.Local);

    private static string FormatRemaining(TimeSpan ts)
    {
        if (ts.TotalDays >= 1)    return $"{(int)ts.TotalDays}d {ts.Hours}h {ts.Minutes}m";
        if (ts.TotalHours >= 1)   return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        if (ts.TotalMinutes >= 1) return $"{(int)ts.TotalMinutes}m {ts.Seconds}s";
        return $"{ts.Seconds}s";
    }

    private void ShowError(string message)
    {
        _errorLabel.Text      = message;
        _errorLabel.IsVisible = true;
    }
}
