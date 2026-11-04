using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using System.IO;
using Avalonia.Platform.Storage;

namespace GTA.App;

public sealed class TaskBuilderWindow : Window
{
    private readonly LlmChatWindow  _owner;
    private readonly ChatSession?   _existingSession;
    private readonly List<TaskUnit> _units = new();

    private readonly StackPanel _unitListPanel;
    private readonly List<Border> _unitRowBorders = new();
    private int _selectedIndex = -1;

    private readonly TextBlock _errorLabel;
    private readonly Button    _removeBtn;
    private readonly Button    _editBtn;

    private readonly List<string>    _globalAttachments    = new();
    private readonly HashSet<string> _globalPreloadedTools = new(StringComparer.OrdinalIgnoreCase);
    private StackPanel? _globalAttachmentChipsPanel;
    private Border?     _globalAttachmentStripBorder;
    private Button?      _globalAttachBtn;
    private Button?      _globalToolsBtn;

    private readonly DatePicker _datePicker;
    private readonly TimePicker _timePicker;

    public TaskBuilderWindow(LlmChatWindow owner, ChatSession? existingSession = null)
    {
        _owner           = owner;
        _existingSession = existingSession;

        if (existingSession != null)
            foreach (var u in existingSession.TaskUnits)
                _units.Add(CloneUnit(u));

        Title                 = existingSession != null ? "📋 Edit Task Blueprint" : "📋 Build a Task";
        Width                 = 540;
        Height                = 700;
        MinWidth              = 440;
        MinHeight             = 520;
        Background            = LlmChatWindow.BgMain;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize             = true;

        // ── Header ─────────────────────────────────────────────────────────
        var titleBlock = new TextBlock
        {
            Text       = existingSession != null ? "Edit Task Blueprint" : "Build a Task",
            FontSize   = 18,
            FontWeight = FontWeight.SemiBold,
            Foreground = LlmChatWindow.ColText,
        };

        var subtitle = new TextBlock
        {
            Text         = "A Task is a container that runs its steps in strict order, " +
                           "1 → 2 → 3 → … Each \"💬 Prompt\" step is sent to the LLM and fully " +
                           "finishes before the next step starts; each \"⏱ Wait\" step just pauses " +
                           "for a fixed delay. Clicking \"▶ Run Task\" starts at step 1 immediately.",
            FontSize     = 11,
            Foreground   = LlmChatWindow.ColMuted,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 6, 0, 0),
        };

        var headerPanel = new StackPanel { Margin = new Thickness(20, 18, 20, 6) };
        headerPanel.Children.Add(titleBlock);
        headerPanel.Children.Add(subtitle);
        DockPanel.SetDock(headerPanel, Dock.Top);

        // ── "Add" buttons ───────────────────────────────────────────────────
        var addPromptBtn = new Button
        {
            Content = "➕  Create Prompt", FontSize = 12, Padding = new Thickness(12, 6, 12, 6),
        };
        UiTheme.StyleButton(addPromptBtn, LlmChatWindow.ColAccent, Brushes.White);
        ToolTip.SetTip(addPromptBtn, "Add a prompt step — sent to the LLM and fully awaited when it runs");
        addPromptBtn.Click += async (_, _) => await AddUnitAsync(TaskUnitKind.Prompt);

        var addWaitBtn = new Button
        {
            Content = "➕  Wait", FontSize = 12, Padding = new Thickness(12, 6, 12, 6),
        };
        UiTheme.StyleButton(addWaitBtn, LlmChatWindow.ColScheduledBadgeBg, Brushes.White);
        ToolTip.SetTip(addWaitBtn, "Add a delay step — pauses for a fixed duration before continuing");
        addWaitBtn.Click += async (_, _) => await AddUnitAsync(TaskUnitKind.Wait);

        var addChainBtn = new Button
        {
            Content = "➕  Chain Task", FontSize = 12, Padding = new Thickness(12, 6, 12, 6),
        };
        UiTheme.StyleButton(addChainBtn, LlmChatWindow.ColTaskRunningBadgeBg, LlmChatWindow.ColTaskRunningBadgeFg);
        ToolTip.SetTip(addChainBtn,
            "Add a step that pauses this blueprint until another currently-RUNNING Task finishes, then continues");
        addChainBtn.Click += async (_, _) => await AddChainTaskUnitAsync();

        var addRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(20, 0, 20, 8),
        };
        addRow.Children.Add(addPromptBtn);
        addRow.Children.Add(addWaitBtn);
        addRow.Children.Add(addChainBtn);
        DockPanel.SetDock(addRow, Dock.Top);

        var globalLabel = new TextBlock
        {
            Text       = "🌐  Global Preload — automatically included in every NEW step below",
            FontSize   = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = LlmChatWindow.ColMuted,
            Margin     = new Thickness(20, 2, 20, 4),
        };
        DockPanel.SetDock(globalLabel, Dock.Top);

        _globalAttachBtn = new Button
        {
            Content = "📎  Attach Global Files", FontSize = 12, Padding = new Thickness(12, 6, 12, 6),
        };
        UiTheme.StyleButton(_globalAttachBtn, LlmChatWindow.BgAiBubble, LlmChatWindow.ColText,
            hoverBackground: LlmChatWindow.BgHoverItem, pressedBackground: LlmChatWindow.BgActiveItem,
            borderBrush: LlmChatWindow.ColDivider);
        ToolTip.SetTip(_globalAttachBtn,
            "Attach file(s) automatically included with every NEW prompt step (or just drag & drop files onto this window)");
        _globalAttachBtn.Click += async (_, _) => await OpenGlobalFilePickerAsync();

        _globalToolsBtn = new Button
        {
            Content = "🧩  Preload Global Tools", FontSize = 12, Padding = new Thickness(12, 6, 12, 6),
        };
        UiTheme.StyleButton(_globalToolsBtn, LlmChatWindow.BgAiBubble, LlmChatWindow.ColText,
            hoverBackground: LlmChatWindow.BgHoverItem, pressedBackground: LlmChatWindow.BgActiveItem,
            borderBrush: LlmChatWindow.ColDivider);
        ToolTip.SetTip(_globalToolsBtn, "Pick tools automatically pre-loaded for every NEW prompt step");
        _globalToolsBtn.Click += async (_, _) => await OpenGlobalPreloadToolsPopupAsync();

        var globalBtnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(20, 0, 20, 4),
        };
        globalBtnRow.Children.Add(_globalAttachBtn);
        globalBtnRow.Children.Add(_globalToolsBtn);
        DockPanel.SetDock(globalBtnRow, Dock.Top);

        _globalAttachmentChipsPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        _globalAttachmentStripBorder = new Border
        {
            IsVisible    = false,
            Background   = LlmChatWindow.BgAiBubble,
            CornerRadius = new CornerRadius(8),
            Padding      = new Thickness(8, 6, 8, 6),
            Margin       = new Thickness(20, 0, 20, 8),
            Child        = new ScrollViewer
            {
                Content = _globalAttachmentChipsPanel,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility   = ScrollBarVisibility.Disabled,
                MaxHeight = 60,
            },
        };
        DockPanel.SetDock(_globalAttachmentStripBorder, Dock.Top);
        var scheduleLabel = new TextBlock
        {
            Text       = "⏰  Schedule (optional) — pick a date & time, then use \"⏰ Run Scheduled\" below",
            FontSize   = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = LlmChatWindow.ColMuted,
            Margin     = new Thickness(20, 2, 20, 4),
        };
        DockPanel.SetDock(scheduleLabel, Dock.Top);

        var scheduleDefault = (existingSession?.TaskScheduledAt ?? DateTimeOffset.Now.AddMinutes(5));

        _datePicker = new DatePicker
        {
            SelectedDate = scheduleDefault.Date,
            FontSize     = 13,
        };
        _timePicker = new TimePicker
        {
            SelectedTime = scheduleDefault.TimeOfDay,
            FontSize     = 13,
        };

        var schedulePickerRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing     = 10,
            Margin      = new Thickness(20, 0, 20, 6),
        };
        schedulePickerRow.Children.Add(_datePicker);
        schedulePickerRow.Children.Add(_timePicker);
        DockPanel.SetDock(schedulePickerRow, Dock.Top);

        var scheduleQuickRow = new StackPanel
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
            scheduleQuickRow.Children.Add(chip);
        }
        DockPanel.SetDock(scheduleQuickRow, Dock.Top);

        // ── Blueprint list label ──────────────────────────────────────────
        var listLabel = new TextBlock
        {
            Text       = "Blueprint (executes top to bottom)",
            FontSize   = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = LlmChatWindow.ColMuted,
            Margin     = new Thickness(20, 4, 20, 4),
        };
        DockPanel.SetDock(listLabel, Dock.Top);

        // ── Remove / Edit selected-unit buttons ──────────────────────────────
        _removeBtn = new Button
        {
            Content = "🗑  Remove Selected", FontSize = 12, Padding = new Thickness(12, 6, 12, 6), IsEnabled = false,
        };
        UiTheme.StyleButton(_removeBtn, LlmChatWindow.ColRunningRed, Brushes.White,
            disabledBackground: LlmChatWindow.ColDisabled, disabledForeground: LlmChatWindow.ColMuted);
        _removeBtn.Click += (_, _) => RemoveSelected();

        _editBtn = new Button
        {
            Content = "✎  Edit Selected", FontSize = 12, Padding = new Thickness(12, 6, 12, 6), IsEnabled = false,
        };
        UiTheme.StyleButton(_editBtn, LlmChatWindow.BgAiBubble, LlmChatWindow.ColText,
            hoverBackground: LlmChatWindow.BgHoverItem, pressedBackground: LlmChatWindow.BgActiveItem,
            borderBrush: LlmChatWindow.ColDivider,
            disabledBackground: LlmChatWindow.ColDisabled, disabledForeground: LlmChatWindow.ColMuted);
        _editBtn.Click += async (_, _) => await EditSelectedAsync();

        var unitButtonsRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(20, 0, 20, 10),
        };
        unitButtonsRow.Children.Add(_editBtn);
        unitButtonsRow.Children.Add(_removeBtn);
        DockPanel.SetDock(unitButtonsRow, Dock.Top);

        // ── Error label ───────────────────────────────────────────────────
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

        var runBtn = new Button
        {
            Content    = "▶  Run Task",
            FontSize   = 13,
            FontWeight = FontWeight.SemiBold,
            Padding    = new Thickness(18, 7, 18, 7),
        };
        UiTheme.StyleButton(runBtn, LlmChatWindow.ColAccent, Brushes.White);
        runBtn.Click += (_, _) => TryConfirmRun();

        var scheduleBtn = new Button
        {
            Content    = "⏰  Run Scheduled",
            FontSize   = 13,
            FontWeight = FontWeight.SemiBold,
            Padding    = new Thickness(18, 7, 18, 7),
        };
        UiTheme.StyleButton(scheduleBtn, LlmChatWindow.ColScheduledBadgeBg, Brushes.White);
        ToolTip.SetTip(scheduleBtn,
            "Schedule this blueprint to start running automatically at the chosen date/time above, instead of right now");
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
        footer.Children.Add(runBtn);
        DockPanel.SetDock(footer, Dock.Bottom);

        // ── Scrollable blueprint list (the "fill" element) ────────────────
        _unitListPanel = new StackPanel { Spacing = 4, Margin = new Thickness(20, 0, 20, 8) };
        var listScroll = new ScrollViewer
        {
            Content                       = _unitListPanel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
        };

        var root = new DockPanel { LastChildFill = true };
        root.Children.Add(headerPanel);
        root.Children.Add(addRow);
        root.Children.Add(globalLabel);
        root.Children.Add(globalBtnRow);
        root.Children.Add(_globalAttachmentStripBorder);
        root.Children.Add(scheduleLabel);
        root.Children.Add(schedulePickerRow);
        root.Children.Add(scheduleQuickRow);
        root.Children.Add(listLabel);
        root.Children.Add(unitButtonsRow);
        root.Children.Add(footer);
        root.Children.Add(_errorLabel);
        root.Children.Add(listScroll); // added LAST → fills remaining space

        DragDrop.SetAllowDrop(root, true);
        root.AddHandler(DragDrop.DragOverEvent, OnWindowDragOver);
        root.AddHandler(DragDrop.DropEvent, OnWindowDrop);

        Content = root;

        RefreshList();
    }

    // ── Blueprint list rendering ───────────────────────────────────────────

    private void RefreshList()
    {
        _unitListPanel.Children.Clear();
        _unitRowBorders.Clear();

        for (int i = 0; i < _units.Count; i++)
        {
            int capturedIndex = i;
            var unit = _units[i];

            var indexLabel = new TextBlock
            {
                Text              = $"{i + 1}.",
                FontSize          = 12,
                FontWeight        = FontWeight.SemiBold,
                Foreground        = LlmChatWindow.ColMuted,
                VerticalAlignment = VerticalAlignment.Center,
                Width             = 22,
            };

            var iconLabel = new TextBlock
            {
                Text              = unit.Kind switch
                {
                    TaskUnitKind.Prompt    => "💬",
                    TaskUnitKind.Wait      => "⏱",
                    TaskUnitKind.ChainTask => "⛓",
                    _                      => "❓",
                },
                FontSize          = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(0, 0, 8, 0),
            };

            var summaryLabel = new TextBlock
            {
                Text = unit.Kind switch
                {
                    TaskUnitKind.Prompt    => TruncateForSummary(unit.PromptText),
                    TaskUnitKind.Wait      => unit.WaitSummary,
                    TaskUnitKind.ChainTask => $"Chain after: {unit.ChainTaskSessionTitle ?? "(unknown task)"}",
                    _                      => string.Empty,
                },
                FontSize          = 12,
                Foreground        = LlmChatWindow.ColText,
                TextWrapping      = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0 };
            row.Children.Add(indexLabel);
            row.Children.Add(iconLabel);
            row.Children.Add(summaryLabel);

            if (unit.Kind == TaskUnitKind.Prompt &&
                (unit.Attachments.Count > 0 || unit.PreloadedToolNames.Count > 0))
            {
                var extrasLabel = new TextBlock
                {
                    FontSize          = 11,
                    Foreground        = LlmChatWindow.ColMuted,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin            = new Thickness(8, 0, 0, 0),
                    Text = string.Join("  ", new[]
                    {
                        unit.Attachments.Count        > 0 ? $"📎{unit.Attachments.Count}"        : null,
                        unit.PreloadedToolNames.Count > 0 ? $"🧩{unit.PreloadedToolNames.Count}" : null,
                    }.Where(s => s != null)),
                };
                row.Children.Add(extrasLabel);
            }

            var border = new Border
            {
                CornerRadius    = new CornerRadius(6),
                Padding         = new Thickness(10, 8, 10, 8),
                Margin          = new Thickness(0, 1, 0, 1),
                Cursor          = new Cursor(StandardCursorType.Hand),
                Background      = Brushes.Transparent,
                BorderBrush     = LlmChatWindow.ColDivider,
                BorderThickness = new Thickness(1),
                Child           = row,
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
            border.PointerPressed += (_, _) => SelectUnit(capturedIndex);

            _unitListPanel.Children.Add(border);
            _unitRowBorders.Add(border);
        }

        if (_selectedIndex >= _units.Count)
            _selectedIndex = -1;

        ApplySelectionHighlight();
        RefreshUnitButtons();
    }

    private void SelectUnit(int index)
    {
        _selectedIndex = index;
        ApplySelectionHighlight();
        RefreshUnitButtons();
    }

    private void ApplySelectionHighlight()
    {
        for (int i = 0; i < _unitRowBorders.Count; i++)
            _unitRowBorders[i].Background = i == _selectedIndex ? LlmChatWindow.BgActiveItem : Brushes.Transparent;
    }

    private void RefreshUnitButtons()
    {
        bool hasSelection    = _selectedIndex >= 0 && _selectedIndex < _units.Count;
        _removeBtn.IsEnabled = hasSelection;
        _editBtn.IsEnabled   = hasSelection;
    }

    // ── Add / edit / remove ────────────────────────────────────────────────

    private async Task AddUnitAsync(TaskUnitKind kind)
    {
        var win = kind == TaskUnitKind.Prompt
            ? new TaskUnitEditWindow(kind, null, _globalAttachments, _globalPreloadedTools)
            : new TaskUnitEditWindow(kind);
        await win.ShowDialog(this);

        if (win.Result != null)
        {
            _units.Add(win.Result);
            _selectedIndex = _units.Count - 1;
            RefreshList();
        }
    }

    /// <summary>"Chain Task": opens the prompt editor directly.</summary>
    private async Task AddChainTaskUnitAsync()
    {
        var picker = new RunningTaskPickerWindow(_owner, excludeSessionId: _existingSession?.Id);
        await picker.ShowDialog(this);

        if (picker.Result == null) return; // cancelled, or nothing was ever running

        _units.Add(new TaskUnit
        {
            Kind                  = TaskUnitKind.ChainTask,
            ChainTaskSessionId    = picker.Result.Id,
            ChainTaskSessionTitle = picker.Result.Title,
        });
        _selectedIndex = _units.Count - 1;
        RefreshList();
    }

    private async Task EditSelectedAsync()
    {
        if (_selectedIndex < 0 || _selectedIndex >= _units.Count) return;

        var existing = _units[_selectedIndex];

        if (existing.Kind == TaskUnitKind.ChainTask)
        {
            var picker = new RunningTaskPickerWindow(_owner, excludeSessionId: _existingSession?.Id);
            await picker.ShowDialog(this);

            if (picker.Result != null)
            {
                existing.ChainTaskSessionId    = picker.Result.Id;
                existing.ChainTaskSessionTitle = picker.Result.Title;
                RefreshList();
            }
            return;
        }

        var win = new TaskUnitEditWindow(existing.Kind, existing);
        await win.ShowDialog(this);

        if (win.Result != null)
        {
            _units[_selectedIndex] = win.Result;
            RefreshList();
        }
    }

    private void RemoveSelected()
    {
        if (_selectedIndex < 0 || _selectedIndex >= _units.Count) return;

        _units.RemoveAt(_selectedIndex);
        _selectedIndex = -1;
        RefreshList();
    }

    // ── Confirm ────────────────────────────────────────────────────────────

    private void TryConfirmRun()
    {
        if (_units.Count == 0)
        {
            ShowError("Add at least one step (Create Prompt or Wait) before running.");
            return;
        }

        if (_existingSession != null)
            _owner.RunExistingTask(_existingSession, _units);
        else
            _owner.CreateAndRunTask(_units);

        Close();
    }
    /// <summary>"Run Scheduled": validates the chosen date/time (must be in the future).</summary>
    private void TryConfirmSchedule()
    {
        if (_units.Count == 0)
        {
            ShowError("Add at least one step (Create Prompt or Wait) before scheduling.");
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

        if (_existingSession != null)
            _owner.ScheduleExistingTask(_existingSession, _units, when);
        else
            _owner.CreateAndScheduleTask(_units, when);

        Close();
    }

    private void ShowError(string message)
    {
        _errorLabel.Text      = message;
        _errorLabel.IsVisible = true;
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static TaskUnit CloneUnit(TaskUnit u) => new TaskUnit
    {
        Id                 = u.Id,
        Kind               = u.Kind,
        PromptText         = u.PromptText,
        WaitSeconds        = u.WaitSeconds,
        WaitUntil          = u.WaitUntil,
        Attachments        = new List<string>(u.Attachments),
        PreloadedToolNames = new List<string>(u.PreloadedToolNames),
    };

    private static string TruncateForSummary(string text)
    {
        string oneLine = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return oneLine.Length > 70 ? oneLine[..70] + "…" : oneLine;
    }

    private static string FormatSecondsShort(double seconds)
    {
        if (seconds >= 3600) return $"{seconds / 3600.0:0.##}h";
        if (seconds >= 60)   return $"{seconds / 60.0:0.##}m";
        return $"{seconds:0.##}s";
    }

    // ── Global preload (📎 files / 🧩 tools applied to every NEW Prompt step) ──

    /// <summary>Opens the file picker to attach files to the blueprint.</summary>
    private async Task OpenGlobalFilePickerAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider == null) return;

        var picked = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title          = "Attach Global File",
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
            if (!_globalAttachments.Contains(localPath))
                _globalAttachments.Add(localPath);
        }

        RefreshGlobalAttachmentStrip();
    }

    private void RemoveGlobalAttachment(string path)
    {
        _globalAttachments.Remove(path);
        RefreshGlobalAttachmentStrip();
    }

    private void RefreshGlobalAttachmentStrip()
    {
        if (_globalAttachmentChipsPanel == null || _globalAttachmentStripBorder == null) return;

        _globalAttachmentChipsPanel.Children.Clear();
        foreach (var path in _globalAttachments)
            _globalAttachmentChipsPanel.Children.Add(BuildGlobalAttachmentChip(path));

        _globalAttachmentStripBorder.IsVisible = _globalAttachments.Count > 0;

        if (_globalAttachBtn != null)
            _globalAttachBtn.Content = _globalAttachments.Count > 0
                ? $"📎  Attach Global Files ({_globalAttachments.Count})"
                : "📎  Attach Global Files";
    }

    private Control BuildGlobalAttachmentChip(string filePath)
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
        removeBtn.Click += (_, _) => RemoveGlobalAttachment(capturedPath);
        row.Children.Add(removeBtn);

        return new Border
        {
            Background   = LlmChatWindow.BgCodeBlock,
            CornerRadius = new CornerRadius(6),
            Padding      = new Thickness(8, 4, 8, 4),
            Child        = row,
        };
    }

    /// <summary>Opens the shared "Pre-load Tools" popup backed by this blueprint's own tool set.</summary>
    private async Task OpenGlobalPreloadToolsPopupAsync()
    {
        var popup = new ToolPreloadPopupWindow(_globalPreloadedTools, RefreshGlobalToolsButtonLabel, "Global — every step");
        await popup.ShowDialog(this);
    }

    private void RefreshGlobalToolsButtonLabel()
    {
        if (_globalToolsBtn == null) return;
        _globalToolsBtn.Content = _globalPreloadedTools.Count > 0
            ? $"🧩  Preload Global Tools ({_globalPreloadedTools.Count})"
            : "🧩  Preload Global Tools";
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
            if (!string.IsNullOrEmpty(localPath) && File.Exists(localPath) && !_globalAttachments.Contains(localPath))
                _globalAttachments.Add(localPath);
        }

        RefreshGlobalAttachmentStrip();
        e.Handled = true;
    }
}
