using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace GTA.App;

public sealed class ToolPreloadPopupWindow : Window
{
    private readonly HashSet<string> _selected;
    private readonly Action?         _onChanged;

    private readonly TextBox    _searchBox;
    private readonly StackPanel _categoriesPanel;
    private readonly TextBlock  _summaryLabel;

    // One row per tool checkbox, so the search box can filter by name.
    private readonly List<(string ToolName, CheckBox Box, Expander Owner)> _rows = new();

    // Per-category "checked / total" count labels shown in each Expander header.
    private readonly Dictionary<string, TextBlock> _categoryCountLabels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<CheckBox>> _categoryBoxes  = new(StringComparer.OrdinalIgnoreCase);

    public ToolPreloadPopupWindow(HashSet<string> selectedToolNames, Action? onChanged = null, string? sessionTitle = null)
    {
        _selected  = selectedToolNames;
        _onChanged = onChanged;

        Title                 = string.IsNullOrEmpty(sessionTitle)
            ? "🧩 Pre-load Tools"
            : $"🧩 Pre-load Tools — {sessionTitle}";
        Width                 = 540;
        Height                = 680;
        MinWidth              = 420;
        MinHeight             = 420;
        Background            = LlmChatWindow.BgMain;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        // ── Header ─────────────────────────────────────────────────────────
        var titleBlock = new TextBlock
        {
            Text       = string.IsNullOrEmpty(sessionTitle)
                ? "Pre-load Tools for Next Prompt"
                : $"Pre-load Tools — {sessionTitle}",
            FontSize   = 16,
            FontWeight = FontWeight.SemiBold,
            Foreground = LlmChatWindow.ColText,
        };

        var subtitle = new TextBlock
        {
            Text         = "Checked tools are always added to the tool list sent to the LLM on every " +
                           "message you send in THIS chat from now on (already-included tools are " +
                           "skipped automatically). This selection belongs only to this chat, only for " +
                           "as long as the app keeps running — a new chat starts empty, and restarting " +
                           "the app resets every chat's selection.",
            FontSize     = 11,
            Foreground   = LlmChatWindow.ColMuted,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 4, 0, 0),
        };

        _searchBox = new TextBox
        {
            Watermark = "Filter tool names…",
            FontSize  = 12,
            Margin    = new Thickness(0, 10, 0, 0),
            Padding   = new Thickness(10, 7, 10, 7),
        };
        UiTheme.StyleTextBox(_searchBox, LlmChatWindow.BgAiBubble, LlmChatWindow.ColText,
            borderBrush: LlmChatWindow.ColDivider, focusBorderBrush: LlmChatWindow.ColAccent);
        _searchBox.TextChanged += (_, _) => ApplyFilter();

        var headerPanel = new StackPanel { Margin = new Thickness(16, 14, 16, 6) };
        headerPanel.Children.Add(titleBlock);
        headerPanel.Children.Add(subtitle);
        headerPanel.Children.Add(_searchBox);
        DockPanel.SetDock(headerPanel, Dock.Top);

        _summaryLabel = new TextBlock
        {
            FontSize   = 11,
            Foreground = LlmChatWindow.ColMuted,
            Margin     = new Thickness(16, 0, 16, 8),
        };
        DockPanel.SetDock(_summaryLabel, Dock.Top);

        // ── Footer ─────────────────────────────────────────────────────────
        var uncheckAllBtn = new Button
        {
            Content = "Uncheck All",
            FontSize = 12,
            Padding  = new Thickness(12, 6, 12, 6),
        };
        UiTheme.StyleButton(uncheckAllBtn, LlmChatWindow.ColDisabled, LlmChatWindow.ColText);
        uncheckAllBtn.Click += (_, _) => UncheckAll();

        var closeBtn = new Button
        {
            Content = "Close",
            FontSize = 12,
            Padding  = new Thickness(18, 6, 18, 6),
        };
        UiTheme.StyleButton(closeBtn, LlmChatWindow.ColAccent, Brushes.White);
        closeBtn.Click += (_, _) => Close();

        var footer = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing             = 8,
            Margin              = new Thickness(16, 8, 16, 14),
        };
        footer.Children.Add(uncheckAllBtn);
        footer.Children.Add(closeBtn);
        DockPanel.SetDock(footer, Dock.Bottom);

        // ── Body: scrollable category tree ────────────────────────────────
        _categoriesPanel = new StackPanel { Spacing = 4, Margin = new Thickness(12, 0, 12, 12) };

        var scroll = new ScrollViewer
        {
            Content                       = _categoriesPanel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
        };

        var root = new DockPanel { LastChildFill = true };
        root.Children.Add(headerPanel);
        root.Children.Add(footer);
        root.Children.Add(_summaryLabel);
        root.Children.Add(scroll);

        Content = root;

        BuildTree();
        UpdateSummary();
    }

    // ── Tree construction ─────────────────────────────────────────────────

    private void BuildTree()
    {
        foreach (ToolPreloadCatalog.Category cat in ToolPreloadCatalog.Categories)
        {
            List<string> toolNames = ToolPreloadCatalog.ToolsIn(cat);
            if (toolNames.Count == 0) continue;

            AddCategoryExpander(cat.Name, cat.Description, toolNames);
        }

        // Safety net: any tool that doesn't fall under a known category still
        // shows up here instead of silently vanishing from the popup.
        List<string> leftover = ToolPreloadCatalog.Uncategorized();
        if (leftover.Count > 0)
            AddCategoryExpander("other", "Tools that don't fall under any of the categories above.", leftover);
    }

    private void AddCategoryExpander(string categoryName, string categoryDescription, List<string> toolNames)
    {
        var innerStack = new StackPanel { Spacing = 2, Margin = new Thickness(20, 4, 4, 6) };
        var boxes       = new List<CheckBox>(toolNames.Count);

        foreach (string name in toolNames)
        {
            var cb = new CheckBox
            {
                Content    = name,
                FontSize   = 12,
                Foreground = LlmChatWindow.ColText,
                IsChecked  = _selected.Contains(name),
            };
            UiTheme.StyleCheckBox(cb, LlmChatWindow.ColText);

            string? desc = ToolPreloadCatalog.DescriptionFor(name);
            if (!string.IsNullOrEmpty(desc))
                ToolTip.SetTip(cb, desc.Length > 320 ? desc[..320] + "…" : desc);

            cb.IsCheckedChanged += (_, _) =>
            {
                if (cb.IsChecked == true) _selected.Add(name);
                else                      _selected.Remove(name);

                UpdateCategoryCountLabel(categoryName);
                UpdateSummary();
                _onChanged?.Invoke();
            };

            innerStack.Children.Add(cb);
            boxes.Add(cb);
        }

        _categoryBoxes[categoryName] = boxes;

        var countLabel = new TextBlock
        {
            FontSize          = 10,
            Foreground        = LlmChatWindow.ColMuted,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(8, 0, 0, 0),
        };
        _categoryCountLabels[categoryName] = countLabel;

        var headerPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        headerPanel.Children.Add(new TextBlock
        {
            Text              = categoryName,
            FontSize          = 13,
            FontWeight        = FontWeight.SemiBold,
            Foreground        = LlmChatWindow.ColText,
            VerticalAlignment = VerticalAlignment.Center,
        });
        headerPanel.Children.Add(countLabel);
        ToolTip.SetTip(headerPanel, categoryDescription);

        var contentBorder = new Border
        {
            Background = LlmChatWindow.BgCodeBlock,
            Child       = innerStack,
        };

        var expander = new Expander
        {
            Header      = headerPanel,
            Content     = contentBorder,
            IsExpanded  = false,
            Margin      = new Thickness(0, 0, 0, 2),
        };

        UiTheme.StyleExpander(
            expander,
            headerBackground:  LlmChatWindow.BgAiBubble,
            headerForeground:  LlmChatWindow.ColText,
            contentBackground: LlmChatWindow.BgCodeBlock);

        foreach (CheckBox cb in boxes)
            _rows.Add((((string)cb.Content!), cb, expander));

        _categoriesPanel.Children.Add(expander);
        UpdateCategoryCountLabel(categoryName);
    }

    // ── Live counters ─────────────────────────────────────────────────────

    private void UpdateCategoryCountLabel(string categoryName)
    {
        if (!_categoryBoxes.TryGetValue(categoryName, out List<CheckBox>? boxes)) return;
        int checkedCount = boxes.Count(b => b.IsChecked == true);
        _categoryCountLabels[categoryName].Text = $"({checkedCount}/{boxes.Count})";
    }

    private void UpdateSummary()
    {
        _summaryLabel.Text = _selected.Count == 0
            ? "No tools pre-loaded — the agent's default tool set is unaffected."
            : $"{_selected.Count} tool(s) pre-loaded — will be added (if not already present) to every prompt.";
    }

    private void UncheckAll()
    {
        foreach (var (_, box, _) in _rows)
            box.IsChecked = false; // triggers IsCheckedChanged → clears _selected + refreshes labels
    }

    // ── Filter ────────────────────────────────────────────────────────────

    private void ApplyFilter()
    {
        string query = (_searchBox.Text ?? "").Trim();
        bool   hasQuery = query.Length > 0;

        var visibleOwners = new HashSet<Expander>();

        foreach (var (toolName, box, owner) in _rows)
        {
            bool match = !hasQuery || toolName.Contains(query, StringComparison.OrdinalIgnoreCase);
            box.IsVisible = match;
            if (match) visibleOwners.Add(owner);
        }

        foreach (Control control in _categoriesPanel.Children)
        {
            if (control is not Expander expander) continue;
            bool anyVisible = visibleOwners.Contains(expander);
            expander.IsVisible = anyVisible;
            if (hasQuery && anyVisible)
                expander.IsExpanded = true;
        }
    }
}
