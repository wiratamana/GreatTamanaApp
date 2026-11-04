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
using GTA.Core;

namespace GTA.App;

public sealed class PromptInjectionWindow : Window
{
    private readonly Action<PendingInjection> _onSubmit;

    private readonly TextBox    _textBox;
    private readonly StackPanel _filesPanel;
    private readonly TextBlock  _toolsSummaryLabel;
    private readonly StackPanel _categoriesPanel;

    private readonly List<string>    _attachedFiles = new();
    private readonly HashSet<string> _selectedTools = new(StringComparer.OrdinalIgnoreCase);

    public PromptInjectionWindow(string? sessionTitle, Action<PendingInjection> onSubmit)
    {
        _onSubmit = onSubmit ?? throw new ArgumentNullException(nameof(onSubmit));

        Title = string.IsNullOrEmpty(sessionTitle)
            ? "💉 Inject"
            : $"💉 Inject — {sessionTitle}";
        Width                 = 560;
        Height                = 640;
        MinWidth              = 420;
        MinHeight             = 420;
        Background            = LlmChatWindow.BgMain;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        // ── Header ─────────────────────────────────────────────────────────
        var titleBlock = new TextBlock
        {
            Text       = string.IsNullOrEmpty(sessionTitle)
                ? "Inject into the running turn"
                : $"Inject into the running turn — {sessionTitle}",
            FontSize   = 16,
            FontWeight = FontWeight.SemiBold,
            Foreground = LlmChatWindow.ColText,
        };

        var subtitle = new TextBlock
        {
            Text = "Not sent immediately. Whatever you submit here is queued and only spliced " +
                   "into the conversation at the START of the NEXT agentic round of this chat's " +
                   "current (or next) turn — while the AI is still thinking/calling tools, it will " +
                   "see this as a \"💉 message injection from external source\" the moment that " +
                   "round begins. Harmless to submit even if no turn is running right now — it " +
                   "just waits for the next one.",
            FontSize     = 11,
            Foreground   = LlmChatWindow.ColMuted,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 4, 0, 0),
        };

        var headerPanel = new StackPanel { Margin = new Thickness(16, 14, 16, 6) };
        headerPanel.Children.Add(titleBlock);
        headerPanel.Children.Add(subtitle);
        DockPanel.SetDock(headerPanel, Dock.Top);

        // ── Footer ─────────────────────────────────────────────────────────
        var cancelBtn = new Button
        {
            Content  = "Cancel",
            FontSize = 12,
            Padding  = new Thickness(14, 6, 14, 6),
        };
        UiTheme.StyleButton(cancelBtn, LlmChatWindow.ColDisabled, LlmChatWindow.ColText);
        cancelBtn.Click += (_, _) => Close();

        var submitBtn = new Button
        {
            Content  = "Submit",
            FontSize = 12,
            Padding  = new Thickness(18, 6, 18, 6),
        };
        UiTheme.StyleButton(submitBtn, LlmChatWindow.ColAccent, Brushes.White);
        submitBtn.Click += (_, _) => Submit();

        var footer = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing             = 8,
            Margin              = new Thickness(16, 8, 16, 14),
        };
        footer.Children.Add(cancelBtn);
        footer.Children.Add(submitBtn);
        DockPanel.SetDock(footer, Dock.Bottom);

        // ── Body: text box, attach strip, foldable tool tree ─────────────────
        _textBox = new TextBox
        {
            Watermark     = "Type additional info / instructions for the AI…",
            AcceptsReturn = true,
            TextWrapping  = TextWrapping.Wrap,
            MinHeight     = 90,
            MaxHeight     = 160,
            FontSize      = 13,
            Padding       = new Thickness(10, 8, 10, 8),
            Margin        = new Thickness(16, 8, 16, 8),
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
            },
            handledEventsToo: false,
            routes: RoutingStrategies.Tunnel);

        var attachBtn = new Button
        {
            Content  = "📎  Attach file…",
            FontSize = 12,
            Padding  = new Thickness(10, 6, 10, 6),
        };
        UiTheme.StyleButton(attachBtn, LlmChatWindow.BgAiBubble, LlmChatWindow.ColText,
            hoverBackground: LlmChatWindow.BgHoverItem, borderBrush: LlmChatWindow.ColDivider);
        attachBtn.Click += async (_, _) => await PickFileAsync();

        _filesPanel = new StackPanel { Spacing = 4, Margin = new Thickness(16, 0, 16, 8) };

        var attachRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin      = new Thickness(16, 0, 16, 4),
        };
        attachRow.Children.Add(attachBtn);

        var toolsLabel = new TextBlock
        {
            Text       = "Optionally load additional tool(s) for the AI, starting next round:",
            FontSize   = 12,
            Foreground = LlmChatWindow.ColMuted,
            Margin     = new Thickness(16, 8, 16, 2),
        };

        _toolsSummaryLabel = new TextBlock
        {
            FontSize   = 11,
            Foreground = LlmChatWindow.ColMuted,
            Margin     = new Thickness(16, 0, 16, 4),
        };

        _categoriesPanel = new StackPanel { Spacing = 4, Margin = new Thickness(12, 0, 12, 12) };
        var toolsScroll = new ScrollViewer
        {
            Content                       = _categoriesPanel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
        };

        var bodyStack = new StackPanel();
        bodyStack.Children.Add(_textBox);
        bodyStack.Children.Add(attachRow);
        bodyStack.Children.Add(_filesPanel);
        bodyStack.Children.Add(toolsLabel);
        bodyStack.Children.Add(_toolsSummaryLabel);
        DockPanel.SetDock(bodyStack, Dock.Top);

        var root = new DockPanel { LastChildFill = true };
        root.Children.Add(headerPanel);
        root.Children.Add(footer);
        root.Children.Add(bodyStack);
        root.Children.Add(toolsScroll);

        Content = root;

        BuildToolTree();
        UpdateToolsSummary();
    }

    // ── Tool tree (mirrors ToolPreloadPopupWindow, but a fresh, local, one-shot selection) ──

    private void BuildToolTree()
    {
        foreach (ToolPreloadCatalog.Category cat in ToolPreloadCatalog.Categories)
        {
            List<string> toolNames = ToolPreloadCatalog.ToolsIn(cat);
            if (toolNames.Count == 0) continue;
            AddCategoryExpander(cat.Name, cat.Description, toolNames);
        }

        List<string> leftover = ToolPreloadCatalog.Uncategorized();
        if (leftover.Count > 0)
            AddCategoryExpander("other", "Tools that don't fall under any of the categories above.", leftover);
    }

    private void AddCategoryExpander(string categoryName, string categoryDescription, List<string> toolNames)
    {
        var innerStack = new StackPanel { Spacing = 2, Margin = new Thickness(20, 4, 4, 6) };

        foreach (string name in toolNames)
        {
            var cb = new CheckBox
            {
                Content    = name,
                FontSize   = 12,
                Foreground = LlmChatWindow.ColText,
                IsChecked  = false,
            };
            UiTheme.StyleCheckBox(cb, LlmChatWindow.ColText);

            string? desc = ToolPreloadCatalog.DescriptionFor(name);
            if (!string.IsNullOrEmpty(desc))
                ToolTip.SetTip(cb, desc.Length > 320 ? desc[..320] + "…" : desc);

            cb.IsCheckedChanged += (_, _) =>
            {
                if (cb.IsChecked == true) _selectedTools.Add(name);
                else                      _selectedTools.Remove(name);
                UpdateToolsSummary();
            };

            innerStack.Children.Add(cb);
        }

        var headerPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        headerPanel.Children.Add(new TextBlock
        {
            Text              = categoryName,
            FontSize          = 13,
            FontWeight        = FontWeight.SemiBold,
            Foreground        = LlmChatWindow.ColText,
            VerticalAlignment = VerticalAlignment.Center,
        });
        ToolTip.SetTip(headerPanel, categoryDescription);

        var contentBorder = new Border { Background = LlmChatWindow.BgCodeBlock, Child = innerStack };

        var expander = new Expander
        {
            Header     = headerPanel,
            Content    = contentBorder,
            IsExpanded = false,
            Margin     = new Thickness(0, 0, 0, 2),
        };
        UiTheme.StyleExpander(
            expander,
            headerBackground:  LlmChatWindow.BgAiBubble,
            headerForeground:  LlmChatWindow.ColText,
            contentBackground: LlmChatWindow.BgCodeBlock);

        _categoriesPanel.Children.Add(expander);
    }

    private void UpdateToolsSummary()
    {
        _toolsSummaryLabel.Text = _selectedTools.Count == 0
            ? "No extra tools selected."
            : $"{_selectedTools.Count} tool(s) will be pre-loaded for the AI starting next round.";
    }

    // ── File attachment (local to this popup — independent of the main input box) ──

    private async System.Threading.Tasks.Task PickFileAsync()
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
            AddFileChip(localPath);
        }
    }

    private void AddFileChip(string filePath)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(new TextBlock { Text = "📄", FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock
        {
            Text                = Path.GetFileName(filePath),
            FontSize            = 12,
            Foreground          = LlmChatWindow.ColText,
            VerticalAlignment   = VerticalAlignment.Center,
            MaxWidth            = 340,
            TextTrimming        = TextTrimming.CharacterEllipsis,
        });

        var removeBtn = new Button
        {
            Content         = "×",
            FontSize        = 13,
            Padding         = new Thickness(4, 0, 4, 0),
            BorderThickness = new Thickness(0),
        };
        UiTheme.StyleLinkButton(removeBtn, LlmChatWindow.ColMuted);

        var chip = new Border
        {
            Background   = LlmChatWindow.BgAiBubble,
            CornerRadius = new CornerRadius(6),
            Padding      = new Thickness(8, 4, 8, 4),
            Child        = row,
        };
        row.Children.Add(removeBtn);

        removeBtn.Click += (_, _) =>
        {
            _attachedFiles.Remove(filePath);
            _filesPanel.Children.Remove(chip);
        };

        _filesPanel.Children.Add(chip);
    }

    /// <summary>Folder pasted clipboard images are saved to for THIS popup.</summary>
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
                    AddFileChip(imagePath);
                }
            }
            else
            {
                // No image on the clipboard — fall back to a normal text paste.
                await PasteTextFallbackAsync(clipboard);
            }
        }
        catch
        {
            // Still try a plain text paste so the keystroke isn't a dead end.
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

    // ── Submit ────────────────────────────────────────────────────────────

    private void Submit()
    {
        string text = (_textBox.Text ?? string.Empty).Trim();

        // Nothing at all typed/attached/checked → nothing meaningful to queue.
        if (text.Length == 0 && _attachedFiles.Count == 0 && _selectedTools.Count == 0)
        {
            Close();
            return;
        }

        var injection = new PendingInjection
        {
            Text            = text,
            Files           = new List<string>(_attachedFiles),
            ToolNamesToLoad = _selectedTools.ToList(),
        };

        _onSubmit(injection);
        Close();
    }
}
