using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace GTA.App;

/// <summary>
/// The "🧩 Pre-load Tools" window. It is only a thin shell (title, footer, Close):
/// the real UI is <see cref="ToolPickerPanel"/>, the universal tool picker that every tool-loader GUI shares.
/// Used by: chat window, scheduler prompt, task builder (global), task step, waiting task step.
/// </summary>
public sealed class ToolPreloadPopupWindow : Window
{
    private readonly HashSet<string> _selected;
    private readonly TextBlock       _summaryLabel;

    public ToolPreloadPopupWindow(HashSet<string> selectedToolNames, Action? onChanged = null, string? sessionTitle = null)
    {
        _selected = selectedToolNames;

        string heading = string.IsNullOrEmpty(sessionTitle)
            ? "Pre-load Tools for Next Prompt"
            : $"Pre-load Tools — {sessionTitle}";

        Title                 = string.IsNullOrEmpty(sessionTitle) ? "🧩 Pre-load Tools" : $"🧩 Pre-load Tools — {sessionTitle}";
        Width                 = 700;
        Height                = 780;
        MinWidth              = 520;
        MinHeight             = 520;
        Background            = LlmChatWindow.BgMain;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        // ── Header ─────────────────────────────────────────────────────────
        var titleBlock = new TextBlock
        {
            Text       = heading,
            FontSize   = 17,
            FontWeight = FontWeight.SemiBold,
            Foreground = LlmChatWindow.ColText,
        };

        var infoBlock = new TextBlock
        {
            Text         = "ⓘ  How this works  (hover)",
            FontSize     = 11,
            Foreground   = LlmChatWindow.ColMuted,
            Margin       = new Thickness(0, 3, 0, 0),
            Cursor       = new Cursor(StandardCursorType.Help),
        };
        ToolTip.SetTip(infoBlock,
            "Selected tools are always added to the tool list sent to the LLM on every message you send in " +
            "THIS chat from now on (tools that are already included are skipped automatically).\n\n" +
            "The selection belongs only to this chat. It is saved with the chat, so it stays after an app restart. " +
            "It is cleared only when you un-tick the tool here. A new chat starts empty.\n\n" +
            "Tools the AI loads by itself (load_tools) also stay for the rest of this chat.\n\n" +
            "My presets are different: they are saved on disk and stay forever.");

        var header = new StackPanel { Margin = new Thickness(18, 14, 18, 10), Children = { titleBlock, infoBlock } };
        DockPanel.SetDock(header, Dock.Top);

        // ── Footer ─────────────────────────────────────────────────────────
        _summaryLabel = new TextBlock
        {
            FontSize          = 11.5,
            Foreground        = LlmChatWindow.ColMuted,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming      = TextTrimming.CharacterEllipsis,
        };

        var closeBtn = new Button { Content = "Done", FontSize = 12, Padding = new Thickness(22, 7, 22, 7) };
        UiTheme.StyleButton(closeBtn, LlmChatWindow.ColAccent, Brushes.White);
        closeBtn.Click += (_, _) => Close();

        var footer = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin            = new Thickness(18, 10, 18, 14),
        };
        Grid.SetColumn(_summaryLabel, 0);
        Grid.SetColumn(closeBtn,      1);
        footer.Children.Add(_summaryLabel);
        footer.Children.Add(closeBtn);
        DockPanel.SetDock(footer, Dock.Bottom);

        // ── Body: the universal picker ─────────────────────────────────────
        var picker = new ToolPickerPanel(_selected, () =>
        {
            UpdateSummary();
            onChanged?.Invoke();
        })
        {
            Margin = new Thickness(18, 0, 14, 0),
        };

        var root = new DockPanel { LastChildFill = true };
        root.Children.Add(header);
        root.Children.Add(footer);
        root.Children.Add(picker);
        Content = root;

        UpdateSummary();

        Opened += (_, _) => picker.FocusSearch();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Close(); }
        };
    }

    private void UpdateSummary()
    {
        _summaryLabel.Text = _selected.Count == 0
            ? "No tools pre-loaded — the agent's default tool set is unaffected."
            : $"{_selected.Count} tool(s) pre-loaded — will be added (if not already present) to every prompt.";
    }
}
