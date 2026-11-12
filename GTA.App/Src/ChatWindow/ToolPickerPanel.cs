using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace GTA.App;

/// <summary>
/// THE universal tool picker. Every tool-loader GUI in the app hosts this one control
/// (Pre-load Tools popup, Injection queue, ...), so they all look and behave the same.
///
/// What it gives the user:
///  - "My presets": named tool lists, saved on disk (see <see cref="ToolPresetStore"/>), loaded with ONE click.
///  - Tools shown as compact toggle chips that wrap into rows (no more one-checkbox-per-line scrolling).
///  - Every category is open by default; per-category "All" / "None" buttons.
///  - Search (several words allowed), "Selected only" view, Expand all / Collapse all, Clear all.
///
/// The panel edits the HashSet it is given DIRECTLY (same contract as the old popup), and calls onChanged after each change.
/// </summary>
internal sealed class ToolPickerPanel : UserControl
{
    // ── Colours (all from the app palette; only the chip "selected" tones are new) ──
    private static readonly SolidColorBrush ColSelHover   = UiTheme.Lighten(LlmChatWindow.ColAccent, 0.12);
    private static readonly SolidColorBrush ColSelBorder  = new(Color.FromRgb(140, 150, 255));
    private static readonly SolidColorBrush ColAccentText = new(Color.FromRgb(150, 160, 255));
    private static readonly SolidColorBrush ColWarn       = new(Color.FromRgb(230, 190, 80));
    private static readonly SolidColorBrush ColDanger     = new(Color.FromRgb(224, 96, 96));

    private sealed class Card
    {
        public string         Name     = "";
        public Border         Root     = null!;
        public TextBlock      Glyph    = null!;
        public TextBlock      Count    = null!;
        public WrapPanel      Body     = null!;
        public bool           Expanded = true;
        public bool           AnyVisible = true;
        public List<Chip>     Chips    = new();
    }

    private sealed class Chip
    {
        public string    Name    = "";
        public Border    Box     = null!;
        public TextBlock Tick    = null!;
        public TextBlock Label   = null!;
        public Card      Owner   = null!;
        public bool      Selected;
        public bool      Hover;
        public bool      Matches = true;
    }

    private sealed class PresetChip
    {
        public ToolPreset                 Preset = null!;
        public HashSet<string>            Tools  = null!;
        public Border                     Box    = null!;
        public TextBlock                  Label  = null!;
        public TextBlock                  Count  = null!;
        public bool                       Hover;
        public bool                       Active;
    }

    private readonly HashSet<string> _selected;
    private readonly Action?         _onChanged;

    private readonly TextBox     _search;
    private readonly StackPanel  _cardsPanel;
    private readonly TextBlock   _noMatch;
    private readonly WrapPanel   _presetWrap;
    private readonly TextBlock   _presetHint;
    private readonly TextBlock   _countText;
    private readonly TextBlock   _statusText;
    private readonly Border      _selectedOnlyBtn;

    private readonly List<Card>        _cards       = new();
    private readonly List<Chip>        _chips       = new();
    private readonly List<PresetChip>  _presetChips = new();

    private bool             _selectedOnly;
    private DispatcherTimer? _statusTimer;

    public ToolPickerPanel(HashSet<string> selected, Action? onChanged = null)
    {
        _selected  = selected ?? throw new ArgumentNullException(nameof(selected));
        _onChanged = onChanged;

        // ── 1. My presets ──────────────────────────────────────────────────
        var presetTitle = new TextBlock
        {
            Text              = "⭐  MY PRESETS",
            FontSize          = 11,
            FontWeight        = FontWeight.Bold,
            Foreground        = LlmChatWindow.ColMuted,
            VerticalAlignment = VerticalAlignment.Center,
        };

        Border saveBtn = MakeActionChip(
            "💾  Save selection as preset",
            "Save the tools that are ticked now under a name.\nIt is stored on disk and stays after you restart the app.",
            () => SaveAsPresetAsync(null),
            accent: true);

        var presetHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(presetTitle, 0);
        Grid.SetColumn(saveBtn,     1);
        presetHeader.Children.Add(presetTitle);
        presetHeader.Children.Add(saveBtn);

        _presetWrap = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };

        _presetHint = new TextBlock
        {
            FontSize     = 10.5,
            Foreground   = LlmChatWindow.ColMuted,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 2, 0, 0),
        };

        var presetCard = new Border
        {
            Background      = LlmChatWindow.BgThinking,
            BorderBrush     = LlmChatWindow.ColDivider,
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(10),
            Padding         = new Thickness(12, 10, 12, 8),
            Child           = new StackPanel { Children = { presetHeader, _presetWrap, _presetHint } },
        };

        // ── 2. Search + toolbar ────────────────────────────────────────────
        _search = new TextBox
        {
            Watermark = "🔍  Filter tools or categories…   (e.g. \"box delete\")",
            FontSize  = 12,
            Padding   = new Thickness(10, 7, 10, 7),
            Margin    = new Thickness(0, 10, 0, 0),
        };
        UiTheme.StyleTextBox(_search, LlmChatWindow.BgAiBubble, LlmChatWindow.ColText,
            borderBrush: LlmChatWindow.ColDivider, focusBorderBrush: LlmChatWindow.ColAccent);
        _search.TextChanged += (_, _) => ApplyFilter();
        _search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !string.IsNullOrEmpty(_search.Text))
            {
                _search.Text = "";
                e.Handled    = true; // do not also close the window
            }
        };

        _countText = new TextBlock
        {
            FontSize          = 12,
            FontWeight        = FontWeight.SemiBold,
            Foreground        = LlmChatWindow.ColText,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _selectedOnlyBtn = MakeActionChip("Selected only", "Show only the tools that are ticked.", ToggleSelectedOnly);
        Border expandBtn   = MakeActionChip("Expand all",   "Open every category.",  () => SetAllExpanded(true));
        Border collapseBtn = MakeActionChip("Collapse all", "Close every category.", () => SetAllExpanded(false));
        Border clearBtn    = MakeActionChip("Clear all",    "Untick every tool.",    ClearAll, danger: true);

        var actions = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            Spacing             = 6,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children            = { _selectedOnlyBtn, expandBtn, collapseBtn, clearBtn },
        };

        var toolbar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin            = new Thickness(0, 10, 0, 8),
        };
        Grid.SetColumn(_countText, 0);
        Grid.SetColumn(actions,    1);
        toolbar.Children.Add(_countText);
        toolbar.Children.Add(actions);

        // ── 3. Category cards ──────────────────────────────────────────────
        _noMatch = new TextBlock
        {
            Text                = "No tool matches your filter.",
            FontSize            = 12,
            Foreground          = LlmChatWindow.ColMuted,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin              = new Thickness(0, 24, 0, 0),
            IsVisible           = false,
        };

        _cardsPanel = new StackPanel { Margin = new Thickness(0, 0, 8, 4) };
        _cardsPanel.Children.Add(_noMatch);

        var scroll = new ScrollViewer
        {
            Content                       = _cardsPanel,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility   = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };

        // ── 4. Status line ─────────────────────────────────────────────────
        _statusText = new TextBlock
        {
            FontSize     = 11,
            Foreground   = LlmChatWindow.ColMuted,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(2, 6, 0, 0),
            MinHeight    = 16,
        };

        // ── Assemble ───────────────────────────────────────────────────────
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto") };
        Grid.SetRow(presetCard,  0);
        Grid.SetRow(_search,     1);
        Grid.SetRow(toolbar,     2);
        Grid.SetRow(scroll,      3);
        Grid.SetRow(_statusText, 4);
        root.Children.Add(presetCard);
        root.Children.Add(_search);
        root.Children.Add(toolbar);
        root.Children.Add(scroll);
        root.Children.Add(_statusText);
        Content = root;

        BuildCards();
        SyncAllChips();
        RebuildPresets();

        if (ToolPresetStore.LoadWarning != null)
            ShowStatus(ToolPresetStore.LoadWarning, StatusKind.Warn);

        // Keep every open picker in sync with the one shared preset file.
        AttachedToVisualTree += (_, _) =>
        {
            ToolPresetStore.Changed -= OnPresetsChanged;
            ToolPresetStore.Changed += OnPresetsChanged;
            RebuildPresets();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            ToolPresetStore.Changed -= OnPresetsChanged;
            _statusTimer?.Stop();
        };
    }

    // ═════════════════════════════════════════════════════════════════════
    //  Public API
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>Replaces the whole selection (also with names that are not in the catalog). Does NOT call onChanged.</summary>
    public void SetSelection(IEnumerable<string> names)
    {
        _selected.Clear();
        foreach (string n in names) _selected.Add(n);
        SyncAllChips();
        UpdatePresetHighlights();
    }

    public void FocusSearch() => _search.Focus();

    // ═════════════════════════════════════════════════════════════════════
    //  Category cards + tool chips
    // ═════════════════════════════════════════════════════════════════════

    private void BuildCards()
    {
        foreach (ToolPreloadCatalog.Category cat in ToolPreloadCatalog.Categories)
        {
            List<string> names = ToolPreloadCatalog.ToolsIn(cat);
            if (names.Count == 0) continue;
            AddCard(cat.Name, cat.Description, names);
        }

        // Safety net: a tool without a category must still show up.
        List<string> leftover = ToolPreloadCatalog.Uncategorized();
        if (leftover.Count > 0)
            AddCard("other", "Tools that don't fall under any of the categories above.", leftover);
    }

    private void AddCard(string categoryName, string description, List<string> toolNames)
    {
        var card = new Card { Name = categoryName };

        card.Glyph = new TextBlock
        {
            Text              = "▾",
            FontSize          = 11,
            Foreground        = LlmChatWindow.ColMuted,
            Width             = 16,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var nameText = new TextBlock
        {
            Text              = categoryName,
            FontSize          = 13,
            FontWeight        = FontWeight.SemiBold,
            Foreground        = LlmChatWindow.ColText,
            VerticalAlignment = VerticalAlignment.Center,
        };

        card.Count = new TextBlock
        {
            FontSize          = 11,
            Foreground        = LlmChatWindow.ColMuted,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(10, 1, 0, 0),
        };

        Border allBtn  = MakeMiniButton("All",  "Tick every tool of this category (only the ones that match the filter).");
        Border noneBtn = MakeMiniButton("None", "Untick every tool of this category.");
        allBtn.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(allBtn).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            SetMany(card.Chips.Where(c => c.Matches), true);
        };
        noneBtn.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(noneBtn).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            SetMany(card.Chips.Where(c => c.Matches), false);
        };

        var titleRow = new StackPanel
        {
            Orientation       = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children          = { nameText, card.Count },
        };

        var headerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
        Grid.SetColumn(card.Glyph, 0);
        Grid.SetColumn(titleRow,   1);
        Grid.SetColumn(allBtn,     2);
        Grid.SetColumn(noneBtn,    3);
        noneBtn.Margin = new Thickness(4, 0, 0, 0);
        headerGrid.Children.Add(card.Glyph);
        headerGrid.Children.Add(titleRow);
        headerGrid.Children.Add(allBtn);
        headerGrid.Children.Add(noneBtn);

        var header = new Border
        {
            Background = Brushes.Transparent,   // so the whole row is clickable
            Padding    = new Thickness(12, 9, 10, 9),
            Cursor     = new Cursor(StandardCursorType.Hand),
            Child      = headerGrid,
        };
        ToolTip.SetTip(header, description);
        header.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(header).Properties.IsLeftButtonPressed) return;
            card.Expanded = !card.Expanded;
            UpdateCardExpansion(card, ForceOpen());
        };

        card.Body = new WrapPanel { Margin = new Thickness(12, 0, 6, 4) };
        foreach (string name in toolNames)
        {
            Chip chip = BuildChip(card, name);
            card.Chips.Add(chip);
            _chips.Add(chip);
            card.Body.Children.Add(chip.Box);
        }

        card.Root = new Border
        {
            Background      = LlmChatWindow.BgSidebar,
            BorderBrush     = LlmChatWindow.ColDivider,
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(10),
            Margin          = new Thickness(0, 0, 0, 8),
            Child           = new StackPanel { Children = { header, card.Body } },
        };

        _cards.Add(card);
        _cardsPanel.Children.Insert(_cardsPanel.Children.Count - 1, card.Root); // keep "no match" label last
    }

    private Chip BuildChip(Card owner, string toolName)
    {
        // TextBlock (not CheckBox/Button content): a plain TextBlock keeps the "_" in names like box_delete_file.
        var tick = new TextBlock
        {
            Text              = "✓",
            FontSize          = 11,
            FontWeight        = FontWeight.Bold,
            Foreground        = Brushes.White,
            Margin            = new Thickness(0, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible         = false,
        };
        var label = new TextBlock
        {
            Text              = toolName,
            FontSize          = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var box = new Border
        {
            CornerRadius    = new CornerRadius(13),
            BorderThickness = new Thickness(1),
            Padding         = new Thickness(10, 4, 12, 4),
            Margin          = new Thickness(0, 0, 6, 6),
            Cursor          = new Cursor(StandardCursorType.Hand),
            Child           = new StackPanel { Orientation = Orientation.Horizontal, Children = { tick, label } },
        };

        string? desc = ToolPreloadCatalog.DescriptionFor(toolName);
        ToolTip.SetTip(box, string.IsNullOrEmpty(desc)
            ? toolName
            : toolName + "\n\n" + (desc.Length > 320 ? desc[..320] + "…" : desc));

        var chip = new Chip { Name = toolName, Box = box, Tick = tick, Label = label, Owner = owner };

        box.PointerEntered += (_, _) => { chip.Hover = true;  Paint(chip); };
        box.PointerExited  += (_, _) => { chip.Hover = false; Paint(chip); };
        box.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(box).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            if (!_selected.Remove(toolName)) _selected.Add(toolName);
            AfterSelectionChanged();
        };

        Paint(chip);
        return chip;
    }

    private static void Paint(Chip c)
    {
        if (c.Selected)
        {
            c.Box.Background  = c.Hover ? ColSelHover : LlmChatWindow.ColAccent;
            c.Box.BorderBrush = ColSelBorder;
            c.Label.Foreground = Brushes.White;
            c.Tick.IsVisible   = true;
        }
        else
        {
            c.Box.Background  = c.Hover ? LlmChatWindow.BgHoverItem : LlmChatWindow.BgAiBubble;
            c.Box.BorderBrush = c.Hover ? LlmChatWindow.ColMuted    : LlmChatWindow.ColDivider;
            c.Label.Foreground = LlmChatWindow.ColText;
            c.Tick.IsVisible   = false;
        }
    }

    // ═════════════════════════════════════════════════════════════════════
    //  Selection bookkeeping
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>Repaints every chip from the shared HashSet (the set is the single source of truth) and refreshes the counters.</summary>
    private void SyncAllChips()
    {
        foreach (Chip c in _chips)
        {
            c.Selected = _selected.Contains(c.Name);
            Paint(c);
        }

        foreach (Card card in _cards)
        {
            int sel = card.Chips.Count(c => c.Selected);
            card.Count.Text       = $"{sel} / {card.Chips.Count}";
            card.Count.Foreground = sel > 0 ? ColAccentText : LlmChatWindow.ColMuted;
            card.Count.FontWeight = sel > 0 ? FontWeight.SemiBold : FontWeight.Normal;
        }

        _countText.Text = _selected.Count == 0
            ? "No tools selected"
            : $"{_selected.Count} tool(s) selected";
        _countText.Foreground = _selected.Count == 0 ? LlmChatWindow.ColMuted : LlmChatWindow.ColText;
    }

    private void AfterSelectionChanged()
    {
        SyncAllChips();
        UpdatePresetHighlights();
        _onChanged?.Invoke();
    }

    private void SetMany(IEnumerable<Chip> chips, bool value)
    {
        bool changed = false;
        foreach (string name in chips.Select(c => c.Name).Distinct().ToList())
        {
            changed |= value ? _selected.Add(name) : _selected.Remove(name);
        }
        if (changed) AfterSelectionChanged();
    }

    private void ClearAll()
    {
        if (_selected.Count == 0) return;
        _selected.Clear();
        AfterSelectionChanged();
        ShowStatus("Selection cleared.", StatusKind.Info);
    }

    // ═════════════════════════════════════════════════════════════════════
    //  Filter / expand
    // ═════════════════════════════════════════════════════════════════════

    private bool ForceOpen() => _selectedOnly || !string.IsNullOrWhiteSpace(_search.Text);

    private void ApplyFilter()
    {
        string[] tokens = (_search.Text ?? "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool hasQuery = tokens.Length > 0;
        bool force    = ForceOpen();
        bool anyCard  = false;

        foreach (Card card in _cards)
        {
            bool categoryHit = hasQuery && tokens.All(t => card.Name.Contains(t, StringComparison.OrdinalIgnoreCase));

            foreach (Chip chip in card.Chips)
            {
                bool textOk = !hasQuery || categoryHit ||
                              tokens.All(t => chip.Name.Contains(t, StringComparison.OrdinalIgnoreCase));
                bool selOk  = !_selectedOnly || chip.Selected;

                chip.Matches       = textOk && selOk;
                chip.Box.IsVisible = chip.Matches;
            }

            card.AnyVisible      = card.Chips.Any(c => c.Matches);
            card.Root.IsVisible  = card.AnyVisible;
            anyCard             |= card.AnyVisible;
            UpdateCardExpansion(card, force);
        }

        _noMatch.Text      = _selectedOnly && !hasQuery ? "Nothing is selected yet." : "No tool matches your filter.";
        _noMatch.IsVisible = !anyCard;
    }

    private static void UpdateCardExpansion(Card card, bool forceOpen)
    {
        bool open = card.Expanded || forceOpen;
        card.Body.IsVisible = open;
        card.Glyph.Text     = open ? "▾" : "▸";
    }

    private void SetAllExpanded(bool expanded)
    {
        foreach (Card card in _cards)
        {
            card.Expanded = expanded;
            UpdateCardExpansion(card, ForceOpen());
        }
    }

    private void ToggleSelectedOnly()
    {
        _selectedOnly = !_selectedOnly;
        PaintToggle(_selectedOnlyBtn, _selectedOnly);
        ApplyFilter();
    }

    // ═════════════════════════════════════════════════════════════════════
    //  Presets
    // ═════════════════════════════════════════════════════════════════════

    private void OnPresetsChanged()
    {
        if (Dispatcher.UIThread.CheckAccess()) RebuildPresets();
        else Dispatcher.UIThread.Post(RebuildPresets);
    }

    private void RebuildPresets()
    {
        _presetWrap.Children.Clear();
        _presetChips.Clear();

        IReadOnlyList<ToolPreset> all = ToolPresetStore.GetAll();

        _presetHint.Text = all.Count == 0
            ? "No presets yet. Tick the tools you want, then press “Save selection as preset”. " +
              "It is saved on disk, so you can load it again any time with one click."
            : "Click = load preset   ·   Ctrl+Click = add to current selection   ·   Right-click = rename / overwrite / delete";

        foreach (ToolPreset p in all)
        {
            PresetChip pc = BuildPresetChip(p);
            _presetChips.Add(pc);
            _presetWrap.Children.Add(pc.Box);
        }

        UpdatePresetHighlights();
    }

    private PresetChip BuildPresetChip(ToolPreset preset)
    {
        var label = new TextBlock
        {
            Text              = preset.Name,
            FontSize          = 12.5,
            FontWeight        = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var count = new TextBlock
        {
            Text              = preset.Tools.Count.ToString(),
            FontSize          = 11,
            Margin            = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var box = new Border
        {
            CornerRadius    = new CornerRadius(15),
            BorderThickness = new Thickness(1),
            Padding         = new Thickness(13, 6, 13, 6),
            Margin          = new Thickness(0, 0, 8, 8),
            Cursor          = new Cursor(StandardCursorType.Hand),
            Child           = new StackPanel { Orientation = Orientation.Horizontal, Children = { label, count } },
        };

        var pc = new PresetChip
        {
            Preset = preset,
            Tools  = new HashSet<string>(preset.Tools, StringComparer.OrdinalIgnoreCase),
            Box    = box,
            Label  = label,
            Count  = count,
        };

        // Tooltip: the tool names inside (max 40 shown).
        string names = string.Join(", ", preset.Tools.Take(40)) + (preset.Tools.Count > 40 ? $" … (+{preset.Tools.Count - 40})" : "");
        ToolTip.SetTip(box, $"{preset.Name}  ({preset.Tools.Count} tools)\n\n{names}");

        box.PointerEntered += (_, _) => { pc.Hover = true;  PaintPreset(pc); };
        box.PointerExited  += (_, _) => { pc.Hover = false; PaintPreset(pc); };
        box.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(box).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            ApplyPreset(preset, merge: e.KeyModifiers.HasFlag(KeyModifiers.Control));
        };

        // Right-click menu
        var loadItem = new MenuItem { Header = "▶   Load  (replace selection)" };
        loadItem.Click += (_, _) => ApplyPreset(preset, merge: false);

        var addItem = new MenuItem { Header = "＋   Add to current selection" };
        addItem.Click += (_, _) => ApplyPreset(preset, merge: true);

        var overwriteItem = new MenuItem { Header = "💾   Overwrite with current selection" };
        overwriteItem.Click += async (_, _) => await OverwritePresetAsync(preset);

        var renameItem = new MenuItem { Header = "✎   Rename…" };
        renameItem.Click += async (_, _) => await RenamePresetAsync(preset);

        var deleteItem = new MenuItem { Header = "🗑   Delete" };
        deleteItem.Click += async (_, _) => await DeletePresetAsync(preset);

        var menu = new ContextMenu();
        menu.Items.Add(loadItem);
        menu.Items.Add(addItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(overwriteItem);
        menu.Items.Add(renameItem);
        menu.Items.Add(deleteItem);
        box.ContextMenu = menu;

        PaintPreset(pc);
        return pc;
    }

    private static void PaintPreset(PresetChip pc)
    {
        if (pc.Active)
        {
            pc.Box.Background   = pc.Hover ? ColSelHover : LlmChatWindow.ColAccent;
            pc.Box.BorderBrush  = ColSelBorder;
            pc.Label.Foreground = Brushes.White;
            pc.Count.Foreground = Brushes.White;
        }
        else
        {
            pc.Box.Background   = pc.Hover ? LlmChatWindow.BgActiveItem : LlmChatWindow.BgAiBubble;
            pc.Box.BorderBrush  = pc.Hover ? ColSelBorder : ColAccentText;
            pc.Label.Foreground = LlmChatWindow.ColText;
            pc.Count.Foreground = LlmChatWindow.ColMuted;
        }
    }

    /// <summary>A preset chip is "active" (filled) while the current selection is EXACTLY that preset.</summary>
    private void UpdatePresetHighlights()
    {
        foreach (PresetChip pc in _presetChips)
        {
            pc.Active = pc.Tools.Count == _selected.Count && _selected.All(pc.Tools.Contains);
            PaintPreset(pc);
        }
    }

    private string? ActivePresetName => _presetChips.FirstOrDefault(p => p.Active)?.Preset.Name;

    private void ApplyPreset(ToolPreset preset, bool merge)
    {
        var available = new HashSet<string>(ToolPreloadCatalog.AllToolNames(), StringComparer.OrdinalIgnoreCase);
        List<string> usable = preset.Tools.Where(available.Contains).ToList();
        int missing = preset.Tools.Count - usable.Count;

        if (!merge) _selected.Clear();
        foreach (string n in usable) _selected.Add(n);

        AfterSelectionChanged();

        string text = merge
            ? $"Added preset “{preset.Name}” — {_selected.Count} tool(s) selected now."
            : $"Loaded preset “{preset.Name}” — {usable.Count} tool(s).";
        if (missing > 0)
            text += $"  {missing} tool(s) from the preset do not exist in this app version and were skipped.";

        ShowStatus(text, missing > 0 ? StatusKind.Warn : StatusKind.Ok);
    }

    private static string? ValidateName(string s)
    {
        if (s.Length == 0)  return "Please type a name.";
        if (s.Length > 40)  return "The name is too long (max 40 characters).";
        return null;
    }

    private Window? OwnerWindow => TopLevel.GetTopLevel(this) as Window;

    private async void SaveAsPresetAsync(string? suggestedName)
    {
        if (_selected.Count == 0)
        {
            ShowStatus("Nothing is selected yet. Tick some tools first, then save them as a preset.", StatusKind.Warn);
            return;
        }

        string? name = await SimpleDialog.PromptTextAsync(
            OwnerWindow,
            "Save as preset",
            $"{_selected.Count} tool(s) will be saved under this name. " +
            "If the name already exists, you can overwrite that preset.",
            suggestedName ?? ActivePresetName ?? "",
            "Save",
            ValidateName);
        if (name == null) return;

        ToolPreset? existing = ToolPresetStore.Find(name);
        if (existing != null)
        {
            bool overwrite = await SimpleDialog.ConfirmAsync(
                OwnerWindow, "Overwrite preset?",
                $"A preset called “{existing.Name}” already exists ({existing.Tools.Count} tools). " +
                $"Replace it with the current selection ({_selected.Count} tools)?",
                "Overwrite", danger: true);
            if (!overwrite) return;
            name = existing.Name; // keep the original spelling
        }

        string? error = ToolPresetStore.Save(name, _selected);
        if (error != null) { ShowStatus(error, StatusKind.Error); return; }

        ShowStatus($"Preset “{name}” saved — {_selected.Count} tool(s). You can load it any time.", StatusKind.Ok);
    }

    private async System.Threading.Tasks.Task OverwritePresetAsync(ToolPreset preset)
    {
        if (_selected.Count == 0)
        {
            ShowStatus("Nothing is selected. Tick some tools first, then overwrite the preset.", StatusKind.Warn);
            return;
        }

        bool ok = await SimpleDialog.ConfirmAsync(
            OwnerWindow, "Overwrite preset?",
            $"Replace the {preset.Tools.Count} tool(s) of “{preset.Name}” with the current selection ({_selected.Count} tools)?",
            "Overwrite", danger: true);
        if (!ok) return;

        string? error = ToolPresetStore.Save(preset.Name, _selected);
        ShowStatus(error ?? $"Preset “{preset.Name}” updated — {_selected.Count} tool(s).",
                   error != null ? StatusKind.Error : StatusKind.Ok);
    }

    private async System.Threading.Tasks.Task RenamePresetAsync(ToolPreset preset)
    {
        string? name = await SimpleDialog.PromptTextAsync(
            OwnerWindow, "Rename preset", $"New name for “{preset.Name}”:", preset.Name, "Rename",
            s =>
            {
                string? basic = ValidateName(s);
                if (basic != null) return basic;
                bool clash = !ToolPresetStore.SameName(s, preset.Name) && ToolPresetStore.Find(s) != null;
                return clash ? "A preset with this name already exists." : null;
            });
        if (name == null || name == preset.Name) return;

        string? error = ToolPresetStore.Rename(preset.Name, name);
        ShowStatus(error ?? $"Renamed to “{name}”.", error != null ? StatusKind.Error : StatusKind.Ok);
    }

    private async System.Threading.Tasks.Task DeletePresetAsync(ToolPreset preset)
    {
        bool ok = await SimpleDialog.ConfirmAsync(
            OwnerWindow, "Delete preset?",
            $"Delete the preset “{preset.Name}” ({preset.Tools.Count} tools)? This cannot be undone. " +
            "The tools that are ticked now are not changed.",
            "Delete", danger: true);
        if (!ok) return;

        string? error = ToolPresetStore.Delete(preset.Name);
        ShowStatus(error ?? $"Preset “{preset.Name}” deleted.", error != null ? StatusKind.Error : StatusKind.Info);
    }

    // ═════════════════════════════════════════════════════════════════════
    //  Status line
    // ═════════════════════════════════════════════════════════════════════

    private enum StatusKind { Info, Ok, Warn, Error }

    private void ShowStatus(string text, StatusKind kind)
    {
        _statusText.Text = text;
        _statusText.Foreground = kind switch
        {
            StatusKind.Ok    => ColAccentText,
            StatusKind.Warn  => ColWarn,
            StatusKind.Error => ColDanger,
            _                => LlmChatWindow.ColMuted,
        };

        _statusTimer?.Stop();
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(kind == StatusKind.Error ? 12 : 7) };
        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer?.Stop();
            _statusText.Text = "";
        };
        _statusTimer.Start();
    }

    // ═════════════════════════════════════════════════════════════════════
    //  Small button helpers (Border-based: no theme surprises)
    // ═════════════════════════════════════════════════════════════════════

    private static Border MakeActionChip(string text, string tip, Action click, bool accent = false, bool danger = false)
    {
        var label = new TextBlock
        {
            Text              = text,
            FontSize          = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var box = new Border
        {
            CornerRadius    = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding         = new Thickness(10, 5, 10, 5),
            Cursor          = new Cursor(StandardCursorType.Hand),
            Child           = label,
        };
        ToolTip.SetTip(box, tip);

        IBrush bg      = accent ? LlmChatWindow.ColAccent : Brushes.Transparent;
        IBrush bgHover = accent ? ColSelHover : LlmChatWindow.BgHoverItem;
        IBrush fg      = accent ? Brushes.White : (danger ? ColDanger : LlmChatWindow.ColText);
        IBrush border  = accent ? ColSelBorder : LlmChatWindow.ColDivider;

        var state = new ActionChipState { Bg = bg, BgHover = bgHover, Border = border };

        label.Foreground  = fg;
        box.Background    = bg;
        box.BorderBrush   = border;
        box.Tag           = state;

        box.PointerEntered += (_, _) => { if (!state.On) box.Background = state.BgHover; };
        box.PointerExited  += (_, _) => { if (!state.On) box.Background = state.Bg; };
        box.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(box).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            click();
        };
        return box;
    }

    /// <summary>Look of one toolbar button; also remembers whether it is a toggle that is switched ON.</summary>
    private sealed class ActionChipState
    {
        public IBrush Bg      = Brushes.Transparent;
        public IBrush BgHover = Brushes.Transparent;
        public IBrush Border  = Brushes.Transparent;
        public bool   On;
    }

    private static void PaintToggle(Border b, bool on)
    {
        if (b.Tag is not ActionChipState s) return;
        s.On          = on;
        b.Background  = on ? LlmChatWindow.BgActiveItem : s.Bg;
        b.BorderBrush = on ? LlmChatWindow.ColAccent    : s.Border;
    }

    private static Border MakeMiniButton(string text, string tip)
    {
        var label = new TextBlock
        {
            Text              = text,
            FontSize          = 11,
            Foreground        = LlmChatWindow.ColMuted,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var box = new Border
        {
            Background   = Brushes.Transparent,
            CornerRadius = new CornerRadius(6),
            Padding      = new Thickness(9, 3, 9, 3),
            Cursor       = new Cursor(StandardCursorType.Hand),
            Child        = label,
        };
        ToolTip.SetTip(box, tip);
        box.PointerEntered += (_, _) => { box.Background = LlmChatWindow.BgHoverItem; label.Foreground = LlmChatWindow.ColText; };
        box.PointerExited  += (_, _) => { box.Background = Brushes.Transparent;        label.Foreground = LlmChatWindow.ColMuted; };
        return box;
    }
}
