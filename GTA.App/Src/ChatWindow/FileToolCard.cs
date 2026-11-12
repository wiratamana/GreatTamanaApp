using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace GTA.App;

// Live card for file tools; read / search cards show the tool result and start folded.
public enum FileCardKind { Write, Append, Edit, Insert, Replace, Remove, Read, ReadLines, Search, SearchDir }

public sealed class FileToolCard
{
    // ── Which tools get this widget ───────────────────────────────────────────

    public static bool TryGetKind(string? toolName, out FileCardKind kind)
    {
        switch (toolName)
        {
            case "write_file":      kind = FileCardKind.Write;     return true;
            case "append_to_file":  kind = FileCardKind.Append;    return true;
            case "edit_line":       kind = FileCardKind.Edit;      return true;
            case "insert_line":     kind = FileCardKind.Insert;    return true;
            case "replace_in_file": kind = FileCardKind.Replace;   return true;
            case "remove_line":     kind = FileCardKind.Remove;    return true;
            case "read_file":       kind = FileCardKind.Read;      return true;
            case "read_line":       kind = FileCardKind.ReadLines; return true;
            case "search_line":     kind = FileCardKind.Search;    return true;
            case "search_in_dir":   kind = FileCardKind.SearchDir; return true;
            default:                kind = default;                return false;
        }
    }

    public static bool IsSupported(string? toolName) => TryGetKind(toolName, out _);

    // ── Layout constants ──────────────────────────────────────────────────────

    private const int    PreviewLines      = 12;   // lines visible while folded
    private const int    HighlightLimit    = 400;  // above this many visible lines: plain text (speed)
    private const double LineH             = 18;
    private const double CodeFont          = 12;
    private const double ExpandedMaxHeight = 460;

    // ── Palette ───────────────────────────────────────────────────────────────

    private static readonly FontFamily Mono = new("Consolas, Courier New, monospace");

    private static readonly SolidColorBrush BgCard     = new(Color.FromRgb(27, 27, 40));
    private static readonly SolidColorBrush BgBody     = new(Color.FromRgb(15, 15, 23));
    private static readonly SolidColorBrush BgChip     = new(Color.FromRgb(40, 40, 58));
    private static readonly SolidColorBrush BgToggle   = new(Color.FromRgb(21, 21, 32));
    private static readonly SolidColorBrush BorderCard = new(Color.FromRgb(62, 62, 84));
    private static readonly SolidColorBrush GutterFg   = new(Color.FromRgb(92, 98, 120));
    private static readonly SolidColorBrush BarTrack   = new(Color.FromRgb(45, 45, 64));
    private static readonly SolidColorBrush BarFill    = new(Color.FromRgb(120, 135, 255));
    private static readonly SolidColorBrush CaretBrush = new(Color.FromRgb(120, 135, 255));

    private static readonly SolidColorBrush Green = new(Color.FromRgb(110, 215, 150));
    private static readonly SolidColorBrush Red   = new(Color.FromRgb(240, 120, 120));
    private static readonly SolidColorBrush Amber = new(Color.FromRgb(240, 190, 80));

    private static readonly SolidColorBrush TintAdd = new(Color.FromArgb(48, 60, 200, 110));
    private static readonly SolidColorBrush TintDel = new(Color.FromArgb(48, 235, 90, 90));

    private static readonly SolidColorBrush PillWarnBg = new(Color.FromRgb(58, 48, 20));
    private static readonly SolidColorBrush PillInfoBg = new(Color.FromRgb(40, 44, 70));
    private static readonly SolidColorBrush PillOkBg   = new(Color.FromRgb(24, 64, 48));
    private static readonly SolidColorBrush PillErrBg  = new(Color.FromRgb(74, 30, 34));
    private static readonly SolidColorBrush PillInfoFg = new(Color.FromRgb(150, 165, 235));

    private static readonly Regex PercentRx = new(@"\((\d+(?:\.\d+)?)%\)", RegexOptions.CultureInvariant);

    // ── State ─────────────────────────────────────────────────────────────────

    private enum CardState { Streaming, Pending, Running, Done, Failed, NoResult }

    private readonly struct DL
    {
        public DL(string text, string gutter, char mark, bool isNote = false)
        {
            Text = text; Gutter = gutter; Mark = mark; IsNote = isNote;
        }
        public string Text   { get; }
        public string Gutter { get; }   // line number text, "" when unknown
        public char   Mark   { get; }   // ' ' normal, '+' added, '-' removed
        public bool   IsNote { get; }   // grey italic explanation row, not real code
    }

    private readonly FileCardKind _kind;
    private readonly bool         _live;
    private readonly List<DL>     _lines = new();

    private CardState _state         = CardState.Streaming;
    private string    _path          = string.Empty;
    private CodeHighlighter.Lang _lang = CodeHighlighter.Lang.Plain;
    private bool      _inputComplete;
    private bool      _expanded;
    private bool      _collapsed;
    private bool      _existedChecked;
    private bool?     _existedBefore;
    private double    _percent       = -1;

    // animation
    private readonly DispatcherTimer _timer;
    private bool   _attached;
    private bool   _barDeterminate;
    private double _phase;
    private int    _tick;
    private bool   _caretOn = true;
    private Run?   _caretRun;

    // controls
    private readonly StackPanel  _root;
    private readonly TextBlock   _title;
    private readonly TextBlock   _pathBlock;
    private readonly TextBlock   _chevron;
    private readonly Border      _pill;
    private readonly TextBlock   _pillDot;
    private readonly TextBlock   _pillText;
    private readonly Grid        _bar;
    private readonly ColumnDefinition _barL, _barM, _barR;
    private readonly TextBlock   _fileChipText;
    private readonly TextBlock   _stats;
    private readonly Grid        _metaGrid;
    private readonly Border      _bodyBorder;
    private readonly ScrollViewer _scroll;
    private readonly Grid        _content;
    private readonly StackPanel  _tint;
    private readonly TextBlock   _gutter;
    private readonly TextBlock   _marks;
    private readonly SelectableTextBlock _code;
    private readonly Button      _copyBtn;
    private string   _copyText = string.Empty;
    private int      _copyFlash;
    private readonly Border      _toggleRow;
    private readonly Button      _toggleBtn;
    private readonly SelectableTextBlock _note;

    // header (same design as ToolCallSection)
    private readonly Border          _hdr;
    private readonly TextBlock       _status;
    private readonly SolidColorBrush _statusBrush;
    private readonly TextBlock       _time;
    private readonly SolidColorBrush _hdrFlat;
    private readonly SolidColorBrush _borderBrush;
    private readonly LinearGradientBrush _sweep;
    private readonly GradientStop    _g0, _g1, _g2;
    private readonly Border          _cardBody;     // everything below the header: this is what folds
    private readonly System.Diagnostics.Stopwatch _sw = new();

    // header palette (same colours as ToolCallSection)
    private static readonly Color HdrAccent = Color.FromRgb(115, 165, 225);
    private static readonly Color HdrBase   = Color.FromRgb(27, 31, 46);
    private static readonly Color HdrHot    = Color.FromRgb(38, 50, 78);
    private static readonly Color HdrOk     = Color.FromRgb(100, 200, 140);
    private static readonly Color HdrErr    = Color.FromRgb(235, 105, 105);
    private static readonly Color HdrMuted  = Color.FromRgb(130, 130, 155);

    /// <summary>The control to put into the chat bubble.</summary>
    public Control Root => _root;

    /// <summary>Marker panel handed to ChatSessionRuntime.CurrentToolParamsPanel</summary>
    public StackPanel ParamsSink { get; }

    /// <summary>Same idea for ChatSessionRuntime.CurrentToolResultPanel (live progress lines).</summary>
    public StackPanel ProgressSink { get; }

    // ── Construction ──────────────────────────────────────────────────────────

    public FileToolCard(FileCardKind kind, bool live)
    {
        _kind = kind;
        _live = live;

        ParamsSink   = new StackPanel { Tag = this };
        ProgressSink = new StackPanel { Tag = this };

        // ── Header: same design as ToolCallSection (run_shell, browse_dir …) ──
        //    status dot · icon + tool name · path · [copy] · state pill · time · chevron
        _statusBrush = new SolidColorBrush(HdrAccent);
        _status = new TextBlock
        {
            Text              = "●",
            FontSize          = 10,
            Foreground        = _statusBrush,
            Width             = 14,
            TextAlignment     = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var icon = new TextBlock
        {
            Text              = IconFor(kind),
            Foreground        = IconBrushFor(kind),   // emoji falls back to a mono glyph → would be black on dark bg
            FontSize          = 11,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _title = new TextBlock
        {
            Text              = TitleFor(kind),
            FontSize          = 11,
            FontFamily        = Mono,
            FontWeight        = FontWeight.SemiBold,
            Foreground        = new SolidColorBrush(HdrAccent),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var nameRow = new StackPanel
        {
            Orientation       = Orientation.Horizontal,
            Spacing           = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        nameRow.Children.Add(icon);
        nameRow.Children.Add(_title);

        _pathBlock = new TextBlock
        {
            Text              = "…",
            FontFamily        = Mono,
            FontSize          = 11,
            Foreground        = LlmChatWindow.ColMuted,
            Opacity           = 0.9,
            TextTrimming      = TextTrimming.CharacterEllipsis,
            MaxLines          = 1,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(12, 0, 10, 0),
        };

        // State pill (WRITING / CREATED / 12 LINES …). The dot lives in the status cell, so _pillDot only holds data.
        _pillDot = new TextBlock { Text = "●", FontSize = 8 };
        _pillText = new TextBlock
        {
            FontSize          = 9,
            FontWeight        = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _pill = new Border
        {
            CornerRadius      = new CornerRadius(8),
            Padding           = new Thickness(8, 1, 8, 1),
            Margin            = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child             = _pillText,
        };

        _time = new TextBlock
        {
            Text              = "",
            FontSize          = 10,
            FontFamily        = Mono,
            Foreground        = LlmChatWindow.ColMuted,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(0, 0, 8, 0),
            IsVisible         = false,
        };

        _chevron = new TextBlock
        {
            Text              = "▾",
            FontSize          = 10,
            Foreground        = LlmChatWindow.ColMuted,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // ── Copy button — write_file ONLY (the full text). No other card has one. ──
        _copyBtn = new Button
        {
            Content           = "⧉ Copy",
            FontSize          = 10,
            MinHeight         = 0,    // Fluent default is 32 px → would make the header tall
            MinWidth          = 0,
            Height            = 18,
            Padding           = new Thickness(6, 0, 6, 0),
            Margin            = new Thickness(0, 0, 8, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            BorderThickness   = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor            = new Cursor(StandardCursorType.Hand),
            IsVisible         = false,   // shown once a write_file has some text to copy
        };
        UiTheme.StyleLinkButton(_copyBtn, LlmChatWindow.ColMuted);
        ToolTip.SetTip(_copyBtn, "Copy the full text to the clipboard");
        _copyBtn.Click += async (_, e) =>
        {
            e.Handled = true;
            await CopyToClipboardAsync();
        };

        var headerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,Auto,Auto,Auto") };
        Grid.SetColumn(_status,    0);
        Grid.SetColumn(nameRow,    1);
        Grid.SetColumn(_pathBlock, 2);
        Grid.SetColumn(_copyBtn,   3);
        Grid.SetColumn(_pill,      4);
        Grid.SetColumn(_time,      5);
        Grid.SetColumn(_chevron,   6);
        headerGrid.Children.Add(_status);
        headerGrid.Children.Add(nameRow);
        headerGrid.Children.Add(_pathBlock);
        headerGrid.Children.Add(_copyBtn);
        headerGrid.Children.Add(_pill);
        headerGrid.Children.Add(_time);
        headerGrid.Children.Add(_chevron);

        // ── Thin progress line (only with a real percentage, e.g. a big write) — inside the header ──
        _barL = new ColumnDefinition(new GridLength(0, GridUnitType.Star));
        _barM = new ColumnDefinition(new GridLength(1, GridUnitType.Star));
        _barR = new ColumnDefinition(new GridLength(0, GridUnitType.Star));
        _bar  = new Grid { Height = 2, Background = BarTrack, IsVisible = false, Margin = new Thickness(0, 3, 0, 1) };
        _bar.ColumnDefinitions.Add(_barL);
        _bar.ColumnDefinitions.Add(_barM);
        _bar.ColumnDefinitions.Add(_barR);
        var barFill = new Border { Background = BarFill };
        Grid.SetColumn(barFill, 1);
        _bar.Children.Add(barFill);

        var headerContent = new StackPanel();
        headerContent.Children.Add(headerGrid);
        headerContent.Children.Add(_bar);

        // Soft light that sweeps across the header while the tool is busy (same as ToolCallSection).
        _g0 = new GradientStop { Color = HdrBase, Offset = 0.0 };
        _g1 = new GradientStop { Color = HdrHot,  Offset = 0.5 };
        _g2 = new GradientStop { Color = HdrBase, Offset = 1.0 };
        _sweep = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint   = new RelativePoint(1, 0.5, RelativeUnit.Relative),
        };
        _sweep.GradientStops.Add(_g0);
        _sweep.GradientStops.Add(_g1);
        _sweep.GradientStops.Add(_g2);

        _hdrFlat     = new SolidColorBrush(HdrBase);
        _borderBrush = new SolidColorBrush(Color.FromArgb(70, HdrAccent.R, HdrAccent.G, HdrAccent.B));

        _hdr = new Border
        {
            Background          = live ? _sweep : _hdrFlat,
            BorderBrush         = _borderBrush,
            BorderThickness     = new Thickness(1),
            CornerRadius        = new CornerRadius(8),
            Padding             = new Thickness(8, 3, 10, 3),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Cursor              = new Cursor(StandardCursorType.Hand),
            Child               = headerContent,
        };
        ToolTip.SetTip(_hdr, "Click to expand / collapse");
        _hdr.PointerEntered += (_, _) => _borderBrush.Color = Color.FromArgb(150, HdrAccent.R, HdrAccent.G, HdrAccent.B);
        _hdr.PointerExited  += (_, _) => _borderBrush.Color = Color.FromArgb(70,  HdrAccent.R, HdrAccent.G, HdrAccent.B);
        _hdr.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left) return;
            // A click on the Copy button must not fold / unfold the card.
            if (e.Source is Visual src && (ReferenceEquals(src, _copyBtn) || _copyBtn.IsVisualAncestorOf(src))) return;

            _userToggled = true;
            SetFolded(!_collapsed, animate: true);
        };

        // ── Meta row: file chip + stats ──────────────────────────────────────
        _fileChipText = new TextBlock
        {
            Text         = "…",
            FontFamily   = Mono,
            FontSize     = 11,
            Foreground   = LlmChatWindow.ColText,
            MaxWidth     = 320,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var chip = new Border
        {
            Background        = BgChip,
            CornerRadius      = new CornerRadius(4),
            Padding           = new Thickness(8, 3, 8, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Child             = _fileChipText,
        };
        _stats = new TextBlock
        {
            FontSize            = 11,
            FontFamily          = Mono,
            Foreground          = LlmChatWindow.ColMuted,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment   = VerticalAlignment.Center,
        };
        _metaGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin            = new Thickness(10, 3, 10, 6),
        };
        Grid.SetColumn(chip,   0);
        Grid.SetColumn(_stats, 1);
        _metaGrid.Children.Add(chip);
        _metaGrid.Children.Add(_stats);

        // ── Body: tint layer + [gutter | marks | code] ───────────────────────
        _gutter = new TextBlock
        {
            FontFamily    = Mono,
            FontSize      = CodeFont,
            LineHeight    = LineH,
            Foreground    = GutterFg,
            TextAlignment = TextAlignment.Right,
            Margin        = new Thickness(12, 0, 10, 0),
            IsVisible     = false,
        };
        _marks = new TextBlock
        {
            FontFamily = Mono,
            FontSize   = CodeFont,
            LineHeight = LineH,
            Margin     = new Thickness(6, 0, 4, 0),
            IsVisible  = false,
        };
        _code = new SelectableTextBlock   // selectable: user can select + Ctrl+C the code
        {
            FontFamily   = Mono,
            FontSize     = CodeFont,
            LineHeight   = LineH,
            Foreground   = CodeHighlighter.DefaultBrush,
            TextWrapping = TextWrapping.NoWrap,
            Margin       = new Thickness(4, 0, 24, 0),
        };

        var codeGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto") };
        Grid.SetColumn(_gutter, 0);
        Grid.SetColumn(_marks,  1);
        Grid.SetColumn(_code,   2);
        codeGrid.Children.Add(_gutter);
        codeGrid.Children.Add(_marks);
        codeGrid.Children.Add(_code);

        _tint = new StackPanel { VerticalAlignment = VerticalAlignment.Top };

        _content = new Grid { Margin = new Thickness(0, 6, 0, 6) };
        _content.Children.Add(_tint);
        _content.Children.Add(codeGrid);

        _scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility   = ScrollBarVisibility.Disabled,
            Content                       = _content,
        };
        // Make the green/red row tint reach the full width even for short lines.
        _scroll.PropertyChanged += (_, e) =>
        {
            if (e.Property == Visual.BoundsProperty)
                _content.MinWidth = Math.Max(0, _scroll.Bounds.Width - 1);
        };

        _toggleBtn = new Button
        {
            FontSize            = 11,
            Padding             = new Thickness(16, 5, 16, 5),
            BorderThickness     = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Center,
            Cursor              = new Cursor(StandardCursorType.Hand),
        };
        UiTheme.StyleLinkButton(_toggleBtn, LlmChatWindow.ColMuted);
        _toggleBtn.Click += (_, _) =>
        {
            _expanded = !_expanded;
            Render();
        };
        _toggleRow = new Border
        {
            Background      = BgToggle,
            BorderBrush     = LlmChatWindow.ColDivider,
            BorderThickness = new Thickness(0, 1, 0, 0),
            IsVisible       = false,
            Child           = _toggleBtn,
        };

        var bodyStack = new StackPanel();
        bodyStack.Children.Add(_scroll);
        bodyStack.Children.Add(_toggleRow);

        _bodyBorder = new Border
        {
            Background      = BgBody,
            BorderBrush     = LlmChatWindow.ColDivider,
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(6),
            Margin          = new Thickness(8, 0, 8, 6),
            ClipToBounds    = true,
            Child           = bodyStack,
        };

        _note = new SelectableTextBlock
        {
            FontSize     = 11,
            FontFamily   = Mono,
            Foreground   = LlmChatWindow.ColMuted,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(12, 0, 12, 6),
            IsVisible    = false,
        };

        // ── Assemble: header (always visible) + folding body, like ToolCallSection ──
        var cardStack = new StackPanel();
        cardStack.Children.Add(_metaGrid);
        cardStack.Children.Add(_bodyBorder);
        cardStack.Children.Add(_note);

        _cardBody = new Border
        {
            Background          = BgCard,
            BorderBrush         = BorderCard,
            BorderThickness     = new Thickness(1),
            CornerRadius        = new CornerRadius(6),
            Margin              = new Thickness(0, 3, 0, 4),
            ClipToBounds        = true,   // nothing spills out while the fold animation shrinks it
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child               = cardStack,
        };

        _root = new StackPanel
        {
            // No MinWidth / MaxWidth: the card fills the whole bubble and follows window size.
            Margin              = new Thickness(4, 1, 4, 1),   // same as ToolCallSection → compact list
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _root.Children.Add(_hdr);
        _root.Children.Add(_cardBody);

        // ── Animation timer (only runs while visible AND something is happening) ──
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _timer.Tick += OnTick;
        _root.AttachedToVisualTree += (_, _) =>
        {
            _attached = true;
            UpdateTimer();
        };
        _root.DetachedFromVisualTree += (_, _) =>
        {
            _attached = false;
            _timer.Stop();
        };

        if (live) _sw.Start();

        // Read / search cards and reloaded (replayed) cards start FOLDED (header row only) — compact by default.
        if (IsReadKind(kind) || !live)
        {
            _collapsed          = true;
            _cardBody.IsVisible = false;
            _metaGrid.IsVisible = !IsReadKind(kind);
            _chevron.Text       = "▸";
        }

        SetState(CardState.Streaming);
        Render();
    }

    // ── Public feed ───────────────────────────────────────────────────────────

    /// <summary>Half-finished tool input JSON, while the AI is still typing.</summary>
    public void OnInputStreaming(string json)
    {
        if (_inputComplete) return;
        ApplyInput(json);
        Render();
    }

    /// <summary>The tool_use block ended — full input JSON is known.</summary>
    public void OnInputComplete(string json)
    {
        _inputComplete = true;
        ApplyInput(json);
        if (_state == CardState.Streaming)
            SetState(CardState.Pending);
        Render();
    }

    /// <summary>A live progress line from the executing tool (e.g. "Writing… 1 KB / 2 KB (50%)").</summary>
    public void OnProgress(string text)
    {
        if (_state == CardState.Done || _state == CardState.Failed) return;

        var m = PercentRx.Match(text);
        if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double pct))
            _percent = Math.Clamp(pct / 100.0, 0, 1);

        if (text.StartsWith("✅", StringComparison.Ordinal)) return; // the real result follows right away
        SetState(CardState.Running);
    }

    /// <summary>The tool finished (or was interrupted). <paramref name="result"/> is the raw tool result text.</summary>
    public void SetResult(string? result)
    {
        result ??= string.Empty;
        if (IsReadKind(_kind)) { SetReadResult(result); return; }
        string trimmed = result.TrimStart();
        string first   = FirstLine(trimmed, 300);

        bool interrupted = trimmed.StartsWith("⚠ Interrupted", StringComparison.Ordinal);
        bool noMatch     = _kind == FileCardKind.Replace &&
                           trimmed.StartsWith("No matches found", StringComparison.OrdinalIgnoreCase);
        bool error       = interrupted || noMatch ||
                           trimmed.StartsWith("Error", StringComparison.OrdinalIgnoreCase) ||
                           trimmed.StartsWith("❌", StringComparison.Ordinal) ||
                           trimmed.StartsWith("⚠", StringComparison.Ordinal);

        _inputComplete = true;
        _percent       = -1;
        if (_toolId != null) LiveCards.TryRemove(_toolId, out _);

        if (interrupted)    SetState(CardState.Failed, "INTERRUPTED");
        else if (noMatch)   SetState(CardState.Failed, "NO MATCH");
        else if (error)     SetState(CardState.Failed, "ERROR");
        else                SetState(CardState.Done);

        // Footer: error text in red; for edits the tool's own one-line summary in grey.
        // write_file's "File written successfully: <path>" adds nothing, so it is hidden.
        if (error)
        {
            // The tool's own text is cut at the first line
            _note.Text = noMatch
                ? "No match found — the file was NOT changed. The lines above were not applied."
                : first + (interrupted ? "  (the file was NOT changed)" : string.Empty);
            _note.Foreground = Red;
            _note.IsVisible  = _note.Text.Length > 0;
        }
        else if (_kind != FileCardKind.Write && first.Length > 0)
        {
            _note.Text       = first;
            _note.Foreground = LlmChatWindow.ColMuted;
            _note.IsVisible  = true;
        }
        else
        {
            _note.IsVisible = false;
        }

        Render();

        // Finished OK → minimize to the header line (animated), like the generic tool widget and the thought.
        // A failed tool stays open so the error is seen. The user can open / close it any time.
        if (!error)
        {
            if (!_live) SetFolded(true, animate: false);
            else        AutoMinimizeSoon();
        }
    }

    /// <summary>Folds the card a moment after "done", so the ✓ and the final text can be seen first.</summary>
    private void AutoMinimizeSoon()
    {
        if (_userToggled) return;
        DispatcherTimer.RunOnce(() =>
        {
            if (!_userToggled && !_collapsed && _state == CardState.Done)
                SetFolded(true, animate: true);
        }, TimeSpan.FromMilliseconds(450));
    }

    // ── Fold / unfold (animated) ──────────────────────────────────────────────

    private DispatcherTimer? _foldTimer;
    private bool _userToggled;

    private void SetFolded(bool fold, bool animate)
    {
        _foldTimer?.Stop();
        _foldTimer = null;

        _collapsed    = fold;
        _chevron.Text = fold ? "▸" : "▾";

        if (fold)
        {
            if (animate) _foldTimer = FoldAnimator.Close(_cardBody);
            else         _cardBody.IsVisible = false;
            return;
        }

        if (IsReadKind(_kind))
        {
            // Read / search cards: file chip + stats row only while unfolded; the (lazy) line parsing
            // + colouring happens now, on the first unfold.
            _metaGrid.IsVisible = _bodyBorder.IsVisible;
            RefreshReadLines();
        }
        else
        {
            Render();   // reloaded cards skip drawing while folded (see Render)
        }

        if (animate) _foldTimer = FoldAnimator.Open(_cardBody);
        else
        {
            _cardBody.MaxHeight = double.PositiveInfinity;
            _cardBody.Opacity   = 1;
            _cardBody.IsVisible = true;
        }
    }

    /// <summary>Open the card at once, without animation (used when a replayed card was built because the user clicked its header).</summary>
    internal void ExpandNow()
    {
        _userToggled = true;
        SetFolded(false, animate: false);
    }


    /// <summary>Replay of a saved session whose tool call never got a result.</summary>
    public void MarkNoResult()
    {
        _inputComplete = true;
        SetState(CardState.NoResult);
        Render();
    }

    // Read / search cards

    private readonly List<DL> _resultLines = new();
    private string _resultText    = string.Empty;
    private bool   _resultParsed;
    private string _readHint      = string.Empty;
    private string _resultSummary = string.Empty;

    private static readonly Regex FoundRx = new(
        @"^Found (\d+) match\(es\)(?: in (\d+) file\(s\))?", RegexOptions.CultureInvariant);

    private void SetReadResult(string result)
    {
        string trimmed = result.TrimStart();
        string first   = FirstLine(trimmed, 300);

        bool interrupted = trimmed.StartsWith("⚠ Interrupted", StringComparison.Ordinal);
        bool error       = interrupted ||
                           trimmed.StartsWith("Error", StringComparison.OrdinalIgnoreCase) ||
                           trimmed.StartsWith("❌", StringComparison.Ordinal) ||
                           trimmed.StartsWith("⚠", StringComparison.Ordinal);
        bool searchKind  = _kind == FileCardKind.Search || _kind == FileCardKind.SearchDir;
        bool noMatch     = searchKind &&
                           (trimmed.StartsWith("No matches found", StringComparison.OrdinalIgnoreCase) ||
                            trimmed.StartsWith("No files found",   StringComparison.OrdinalIgnoreCase));
        bool notRepeated = trimmed.StartsWith("[Not repeated]", StringComparison.Ordinal);

        _inputComplete = true;
        _percent       = -1;
        if (_toolId != null) LiveCards.TryRemove(_toolId, out _);

        _resultText   = (error || noMatch || notRepeated) ? string.Empty : result;   // those cases show the grey / red note instead
        _resultParsed = false;
        _resultLines.Clear();

        // Cheap summary for the header pill / meta row (no per-line parsing here).
        _resultSummary = string.Empty;
        if (!error && !noMatch && !notRepeated && trimmed.Length > 0)
        {
            var fm = FoundRx.Match(trimmed);
            if (fm.Success)
            {
                string m = fm.Groups[1].Value + (fm.Groups[1].Value == "1" ? " match" : " matches");
                _resultSummary = fm.Groups[2].Success
                    ? m + " in " + fm.Groups[2].Value + (fm.Groups[2].Value == "1" ? " file" : " files")
                    : m;
            }
            else if (!searchKind)
            {
                int n = 0;
                foreach (char c in trimmed) if (c == '\n') n++;
                if (trimmed[^1] != '\n') n++;
                _resultSummary = LineCountText(n);
            }
        }

        string pill = DoneText() + (_resultSummary.Length > 0 ? " · " + _resultSummary.ToUpperInvariant() : string.Empty);

        if (interrupted)        SetState(CardState.Failed, "INTERRUPTED");
        else if (error)         SetState(CardState.Failed, "ERROR");
        else if (noMatch)       SetState(CardState.Done, "NO MATCH");
        else if (notRepeated)   SetState(CardState.Done, "NOT REPEATED");
        else                    SetState(CardState.Done, pill);

        if (error)
        {
            _note.Text       = first;
            _note.Foreground = Red;
            _note.IsVisible  = _note.Text.Length > 0;
        }
        else if (noMatch || notRepeated)
        {
            _note.Text       = first;
            _note.Foreground = LlmChatWindow.ColMuted;
            _note.IsVisible  = true;
        }
        else
        {
            _note.IsVisible = false;
        }

        // Error / no match / not repeated: there is no text to show, only the note. Hide the empty code area.
        _bodyBorder.IsVisible = !(error || noMatch || notRepeated);

        if (_lastJson != null) ApplyInput(_lastJson);
        else                   { SetReadStats(); _copyText = _resultText; }   // read / search cards have no copy button
        Render();

        // A real error must not stay hidden inside a folded card (live): open it, the note is the message.
        if (error && _collapsed && _live)
            SetFolded(false, animate: true);
    }

    private void SetReadStats()
    {
        var parts = new List<string>();
        if (_readHint.Length > 0)      parts.Add(_readHint);
        if (_resultSummary.Length > 0) parts.Add(_resultSummary);
        SetStats((string.Join(" · ", parts), LlmChatWindow.ColMuted));
    }

    /// <summary>Turns the raw result text into numbered display lines — once, and only when needed.</summary>
    private void EnsureResultLines()
    {
        if (_resultParsed) return;
        _resultParsed = true;
        _resultLines.Clear();
        if (_resultText.Length == 0) return;

        bool noteStyle = _kind == FileCardKind.Search || _kind == FileCardKind.SearchDir;
        string s = _resultText.Replace("\r\n", "\n").Replace('\r', '\n');
        int pos = 0;
        while (pos <= s.Length)
        {
            int nl = s.IndexOf('\n', pos);
            string line = nl < 0 ? s.Substring(pos) : s.Substring(pos, nl - pos);
            bool last   = nl < 0;
            pos         = nl < 0 ? s.Length + 1 : nl + 1;
            if (last && line.Length == 0) break;     // trailing newline is not another line

            // "[012] text" or "  [012] text" (search results) → numbered line.
            int i = 0;
            while (i < line.Length && line[i] == ' ') i++;
            if (i < line.Length && line[i] == '[')
            {
                int j = i + 1;
                while (j < line.Length && line[j] >= '0' && line[j] <= '9') j++;
                if (j > i + 1 && j < line.Length && line[j] == ']' &&
                    int.TryParse(line.AsSpan(i + 1, j - i - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx))
                {
                    int textStart = j + 1;
                    if (textStart < line.Length && line[textStart] == ' ') textStart++;
                    // Tools count from 0; editors (and the Edit card) count from 1.
                    _resultLines.Add(new DL(line.Substring(textStart), (idx + 1).ToString(CultureInfo.InvariantCulture), ' '));
                    continue;
                }
            }
            _resultLines.Add(new DL(line, string.Empty, ' ', isNote: noteStyle));
        }
    }

    /// <summary>Unfold handler for read / search cards: parse + draw now.</summary>
    private void RefreshReadLines()
    {
        EnsureResultLines();
        _lines.Clear();
        _lines.AddRange(_resultLines);
        Render();
    }
    // Old text ("red" lines) for edit_line / remove_line

    // IMPORTANT: this dictionary must NOT be a static field of FileToolCard itself. Touching ANY static field of
    // FileToolCard runs its static constructor, which creates Avalonia brushes (UI thread only). SnapshotBeforeExecute
    // runs on the agent thread BEFORE every tool call, so if no card was ever created yet (first tool of the session is
    // not a file tool), the static constructor ran on the wrong thread, threw "Call from invalid thread", and the whole
    // type stayed broken for the rest of the process (tool card stuck on "Running…", turn aborted).
    private static class LiveCardRegistry
    {
        public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, FileToolCard> Cards = new();
    }

    private static System.Collections.Concurrent.ConcurrentDictionary<string, FileToolCard> LiveCards => LiveCardRegistry.Cards;

    private string?  _toolId;
    private string?  _finalInputJson;   // written on the stream thread
    private string?  _lastJson;         // UI thread
    private string[]? _oldLines;
    private int      _oldStart;
    private bool     _snapshotDone;

    private bool OldTextKnownToBeMissing => !_live || _snapshotDone;

    internal static void Register(string toolId, FileToolCard card)
    {
        if (string.IsNullOrEmpty(toolId)) return;
        card._toolId = toolId;
        LiveCards[toolId] = card;
    }

    /// <summary>Stream thread: the complete input JSON of this tool call.</summary>
    internal void SetFinalInput(string json) => Volatile.Write(ref _finalInputJson, json);

    /// <summary>Stream thread, called right before the tool executes.</summary>
    internal static void SnapshotBeforeExecute(string? toolId)
    {
        if (string.IsNullOrEmpty(toolId) || !LiveCards.TryGetValue(toolId, out FileToolCard? card)) return;
        if (card._kind != FileCardKind.Edit && card._kind != FileCardKind.Remove) return;

        string[]? old   = null;
        int       start = 0;
        try
        {
            string? json = Volatile.Read(ref card._finalInputJson);
            if (json != null)
            {
                var f = PartialJson.ParseTopLevel(json);
                string path = StrOf(f, "path") ?? string.Empty;
                int? li     = IntOf(f, "line_index");
                int  len    = IntOf(f, "length") ?? 1;

                if (path.Length > 0 && li.HasValue && len > 0 && File.Exists(path))
                {
                    string[] all = File.ReadAllLines(path);
                    start = li.Value;
                    if (start >= 0 && start < all.Length)
                    {
                        int cnt = Math.Min(len, all.Length - start);
                        old = new string[cnt];
                        Array.Copy(all, start, old, 0, cnt);
                    }
                }
            }
        }
        catch { old = null; /* best effort — the card shows a grey note instead */ }

        string[]? oldFinal   = old;
        int       startFinal = start;
        Dispatcher.UIThread.Post(() => card.SetOldLines(startFinal, oldFinal));
    }

    private void SetOldLines(int start, string[]? old)
    {
        _snapshotDone = true;
        _oldStart     = start;
        _oldLines     = old;
        if (_lastJson != null)
        {
            ApplyInput(_lastJson);
            Render();
        }
    }

    /// <summary>Adds the old lines as red '-' rows (numbered with their real line numbers).</summary>
    private void AddOldLines()
    {
        if (_oldLines == null) return;
        for (int i = 0; i < _oldLines.Length; i++)
            _lines.Add(new DL(_oldLines[i], (_oldStart + 1 + i).ToString(CultureInfo.InvariantCulture), '-'));
    }


    // ── Input → line model ────────────────────────────────────────────────────

    private void ApplyInput(string json)
    {
        _lastJson = json;
        var f = PartialJson.ParseTopLevel(json);

        if (f.TryGetValue("path", out var pf))
        {
            if (pf.Value.Length > 0 && pf.Value != _path)
            {
                _path = pf.Value;
                _pathBlock.Text = ShortenPath(_path);
                ToolTip.SetTip(_pathBlock, _path);

                string name = Path.GetFileName(_path.TrimEnd('\\', '/'));
                _fileChipText.Text = string.IsNullOrEmpty(name) ? _path : name;
                _lang = CodeHighlighter.Detect(_path);
            }

            // Remember whether the file existed BEFORE the tool runs (live only) —
            // tells "CREATED" from "OVERWRITTEN" once the write succeeded.
            if (_live && !_existedChecked && pf.Complete && _path.Length > 0)
            {
                _existedChecked = true;
                try { _existedBefore = File.Exists(_path); } catch { _existedBefore = null; }
            }
        }

        int? lineIndex = IntOf(f, "line_index");
        int? length    = IntOf(f, "length");

        string copyText = string.Empty;
        _lines.Clear();
        switch (_kind)
        {
            case FileCardKind.Write:
            {
                string content = StrOf(f, "content") ?? string.Empty;
                copyText = content;
                AddLines(content, firstNumber: 1, mark: ' ');
                SetStats((LineCountText(_lines.Count) + " · " + FormatBytes(Encoding.UTF8.GetByteCount(content)), LlmChatWindow.ColMuted));
                break;
            }

            case FileCardKind.Append:
            {
                copyText = StrOf(f, "content") ?? string.Empty;
                AddLines(StrOf(f, "content") ?? string.Empty, firstNumber: null, mark: '+');
                SetStats(("+" + _lines.Count, Green));
                break;
            }

            case FileCardKind.Edit:
            {
                copyText = StrOf(f, "contents") ?? string.Empty;

                // Removed (old) lines first, then the added (new) lines — like a real diff.
                if (_oldLines != null && _oldLines.Length > 0)
                    AddOldLines();
                else if (lineIndex.HasValue && (length ?? 1) > 0 && _inputComplete && OldTextKnownToBeMissing)
                    _lines.Add(new DL($"({length ?? 1} old line(s) replaced — old text not available)",
                                      (lineIndex.Value + 1).ToString(CultureInfo.InvariantCulture), '-', isNote: true));

                int added = AddLines(StrOf(f, "contents") ?? string.Empty, lineIndex.HasValue ? lineIndex + 1 : null, '+');
                int? removed = _oldLines != null ? _oldLines.Length
                             : length.HasValue   ? length
                             : (_inputComplete   ? (int?)1 : null);
                var parts = new List<(string, IBrush)> { ("+" + added, Green) };
                if (removed.HasValue)  parts.Add(("  −" + removed.Value, Red));
                if (lineIndex.HasValue) parts.Add(("   @ line " + (lineIndex.Value + 1), LlmChatWindow.ColMuted));
                SetStats(parts.ToArray());
                break;
            }

            case FileCardKind.Insert:
            {
                copyText = StrOf(f, "contents") ?? string.Empty;
                AddLines(StrOf(f, "contents") ?? string.Empty, lineIndex.HasValue ? lineIndex + 1 : null, '+');
                var parts = new List<(string, IBrush)> { ("+" + _lines.Count, Green) };
                if (lineIndex.HasValue) parts.Add(("   @ line " + (lineIndex.Value + 1), LlmChatWindow.ColMuted));
                SetStats(parts.ToArray());
                break;
            }

            case FileCardKind.Replace:
            {
                int oldCount = AddLines(StrOf(f, "old_text") ?? string.Empty, null, '-');
                int newCount = AddLines(StrOf(f, "new_text") ?? string.Empty, null, '+');
                copyText = StrOf(f, "new_text") ?? string.Empty;
                var parts = new List<(string, IBrush)>
                {
                    ("−" + oldCount, Red),
                    ("  +" + newCount, Green),
                };
                if (f.TryGetValue("use_regex", out var rx) && rx.Value == "true")
                    parts.Add(("   regex", LlmChatWindow.ColMuted));
                SetStats(parts.ToArray());
                break;
            }

            case FileCardKind.Remove:
            {
                int n = length ?? (_inputComplete ? 1 : 0);
                if (_oldLines != null && _oldLines.Length > 0)
                    AddOldLines();
                else if (lineIndex.HasValue && n > 0 && OldTextKnownToBeMissing)
                    _lines.Add(new DL($"({n} line(s) removed, starting at line {lineIndex.Value + 1} — old text not available)", "", '-', isNote: true));
                SetStats(n > 0 ? new[] { ("−" + n, (IBrush)Red) } : Array.Empty<(string, IBrush)>());
                break;
            }

            case FileCardKind.Read:
            case FileCardKind.ReadLines:
            case FileCardKind.Search:
            case FileCardKind.SearchDir:
            {
                // The text of these cards is the tool RESULT (see ApplyResult), not the input.
                // The input only gives a small hint for the meta row (search text / start line).
                string hint = string.Empty;
                if (_kind == FileCardKind.Search || _kind == FileCardKind.SearchDir)
                {
                    string q = StrOf(f, "content") ?? string.Empty;
                    if (q.Length > 0)
                        hint = "\u201C" + (q.Length > 60 ? q[..60] + "…" : q) + "\u201D";
                }
                else if (_kind == FileCardKind.ReadLines)
                {
                    int? st = IntOf(f, "start");
                    int? ln = IntOf(f, "length");
                    if (st.HasValue || ln.HasValue)
                        hint = "from line " + ((st ?? 0) + 1) + (ln.HasValue ? $", {ln.Value} line(s)" : string.Empty);
                }
                _readHint = hint;

                // Folded (the default): nothing to draw yet — see RefreshReadLines.
                if (!_collapsed)
                {
                    EnsureResultLines();
                    _lines.AddRange(_resultLines);
                }
                copyText = _resultText;
                SetReadStats();
                break;
            }
        }

        // Text for the "Copy" button: ONLY write_file has one (the full text, never the folded preview).
        // Edit / insert / replace / remove / read / search cards have no copy button.
        _copyText          = copyText;
        _copyBtn.IsVisible = _kind == FileCardKind.Write && copyText.Length > 0;
    }

    /// <summary>Splits <paramref name="text"/> into lines and appends them. Returns the number of lines added.</summary>
    private int AddLines(string text, int? firstNumber, char mark)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        string s = text.Replace("\r\n", "\n").Replace('\r', '\n');
        int pos = 0;
        int added = 0;
        var local = new List<string>();
        while (true)
        {
            int nl = s.IndexOf('\n', pos);
            if (nl < 0) { local.Add(s.Substring(pos)); break; }
            local.Add(s.Substring(pos, nl - pos));
            pos = nl + 1;
        }
        // A trailing newline is not another line.
        if (local.Count > 0 && local[^1].Length == 0) local.RemoveAt(local.Count - 1);

        foreach (string line in local)
        {
            string gutter = firstNumber.HasValue ? (firstNumber.Value + added).ToString(CultureInfo.InvariantCulture) : string.Empty;
            _lines.Add(new DL(line, gutter, mark));
            added++;
        }
        return added;
    }

    // ── Rendering ─────────────────────────────────────────────────────────────

    private void Render()
    {
        // Read / search cards and reloaded cards start folded. While folded nothing is visible, so skip the
        // (regex colouring) work entirely — it runs once, when the user unfolds the card.
        if (_collapsed && (IsReadKind(_kind) || !_live)) return;

        int total = _lines.Count;
        int start = 0;
        int count = total;

        if (!_expanded && total > PreviewLines)
        {
            count = PreviewLines;
            // While the AI is typing: follow the newest lines ("tail"). When finished: show the top.
            if (_state == CardState.Streaming)
                start = total - PreviewLines;
        }

        bool anyGutter = false, anyMark = false;
        for (int i = start; i < start + count; i++)
        {
            if (_lines[i].Gutter.Length > 0) anyGutter = true;
            if (_lines[i].Mark != ' ')       anyMark   = true;
        }

        // Gutter (line numbers)
        _gutter.IsVisible = anyGutter;
        if (anyGutter)
        {
            var sb = new StringBuilder();
            for (int i = start; i < start + count; i++)
            {
                if (i > start) sb.Append('\n');
                sb.Append(_lines[i].Gutter);
            }
            _gutter.Text = sb.ToString();
        }

        // Marks (+ / −)
        _marks.IsVisible = anyMark;
        var markInlines = _marks.Inlines;
        markInlines?.Clear();
        if (anyMark && markInlines != null)
        {
            if (count <= HighlightLimit)
            {
                for (int i = start; i < start + count; i++)
                {
                    if (i > start) markInlines.Add(new LineBreak());
                    markInlines.Add(new Run(_lines[i].Mark.ToString()) { Foreground = MarkBrush(_lines[i].Mark) });
                }
            }
            else
            {
                // Many lines: one Run per block of equal marks (not one Run per line — speed).
                int runStart = start;
                while (runStart < start + count)
                {
                    char m = _lines[runStart].Mark;
                    var sb = new StringBuilder();
                    int e = runStart;
                    while (e < start + count && _lines[e].Mark == m)
                    {
                        if (e > runStart) sb.Append('\n');
                        sb.Append(m);
                        e++;
                    }
                    if (runStart > start) markInlines.Add(new LineBreak());
                    markInlines.Add(new Run(sb.ToString()) { Foreground = MarkBrush(m) });
                    runStart = e;
                }
            }
        }

        // Row tint (green = added, red = removed) — one Border per run of equal marks.
        // Not drawn when the tool failed: a red/green diff would look like it was applied.
        _tint.Children.Clear();
        if (anyMark && !IsDimmed)
        {
            int runStart = start;
            while (runStart < start + count)
            {
                char m = _lines[runStart].Mark;
                int e = runStart;
                while (e < start + count && _lines[e].Mark == m) e++;
                _tint.Children.Add(new Border
                {
                    Height     = (e - runStart) * LineH,
                    Background = m == '+' ? TintAdd : m == '-' ? TintDel : Brushes.Transparent,
                });
                runStart = e;
            }
        }

        // Code
        _caretRun = null;
        var codeInlines = _code.Inlines;
        if (codeInlines == null)
        {
            var sb = new StringBuilder();
            for (int i = start; i < start + count; i++)
            {
                if (i > start) sb.Append('\n');
                sb.Append(_lines[i].Text.Replace("\t", "    "));
            }
            _code.Text = sb.ToString();
        }
        else
        {
            codeInlines.Clear();

            if (count == 0)
            {
                string hint = _state == CardState.Streaming ? "waiting for the AI to type…"
                            : _kind  == FileCardKind.Remove ? "(nothing to show)"
                            :                                 "(empty)";
                codeInlines.Add(new Run(hint) { Foreground = GutterFg, FontStyle = FontStyle.Italic });
            }
            else if (count <= HighlightLimit)
            {
                for (int i = start; i < start + count; i++)
                {
                    if (i > start) codeInlines.Add(new LineBreak());
                    if (_lines[i].IsNote)
                        codeInlines.Add(new Run(_lines[i].Text) { Foreground = GutterFg, FontStyle = FontStyle.Italic });
                    else
                        CodeHighlighter.AppendLine(codeInlines, _lines[i].Text.Replace("\t", "    "), _lang);
                }
            }
            else
            {
                var sb = new StringBuilder();
                for (int i = start; i < start + count; i++)
                {
                    if (i > start) sb.Append('\n');
                    sb.Append(_lines[i].Text.Replace("\t", "    "));
                }
                codeInlines.Add(new Run(sb.ToString()) { Foreground = CodeHighlighter.DefaultBrush });
            }

            // Blinking caret at the end of the newest line = "the AI is typing here".
            if (_state == CardState.Streaming)
            {
                _caretRun = new Run(_caretOn ? "▍" : " ") { Foreground = CaretBrush };
                codeInlines.Add(_caretRun);
            }
        }

        // Fold / unfold button
        _toggleRow.IsVisible = total > PreviewLines;
        _toggleBtn.Content   = _expanded ? "Show less  ▲" : $"Show all {total} lines  ▼";

        // Scroll behaviour
        _scroll.VerticalScrollBarVisibility = _expanded ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        _scroll.MaxHeight                   = _expanded ? ExpandedMaxHeight : double.PositiveInfinity;
        if (_expanded && _state == CardState.Streaming)
            Dispatcher.UIThread.Post(() => _scroll.ScrollToEnd(), DispatcherPriority.Background);
    }

    /// <summary>True when the tool failed / never ran — the diff was NOT applied, so it must not look like one.</summary>
    private bool IsDimmed => _state == CardState.Failed || _state == CardState.NoResult;

    private IBrush MarkBrush(char mark) =>
        IsDimmed ? GutterFg : mark == '+' ? Green : mark == '-' ? Red : LlmChatWindow.ColMuted;

    private void SetStats(params (string text, IBrush brush)[] parts)
    {
        var inlines = _stats.Inlines;
        if (inlines == null)
        {
            var sb = new StringBuilder();
            foreach (var p in parts) sb.Append(p.text);
            _stats.Text = sb.ToString();
            return;
        }

        inlines.Clear();
        foreach (var p in parts)
            inlines.Add(new Run(p.text) { Foreground = p.brush });
    }

    // ── Status pill / progress bar / animation ────────────────────────────────

    private void SetState(CardState s, string? textOverride = null)
    {
        CardState prev = _state;
        _state = s;

        switch (s)
        {
            case CardState.Streaming:
                SetPill(PillWarnBg, Amber, "●", Verb());
                break;
            case CardState.Pending:
                SetPill(PillInfoBg, PillInfoFg, "•", "QUEUED");
                break;
            case CardState.Running:
                SetPill(PillWarnBg, Amber, "●",
                    _percent >= 0 ? $"{Verb()} {(int)Math.Round(_percent * 100)}%" : Verb());
                break;
            case CardState.Done:
                SetPill(PillOkBg, Green, "✓", textOverride ?? DoneText());
                break;
            case CardState.Failed:
                SetPill(PillErrBg, Red, "✕", textOverride ?? "ERROR");
                break;
            case CardState.NoResult:
                SetPill(PillInfoBg, PillInfoFg, "?", "NO RESULT");
                break;
        }

        bool active      = IsActive;
        bool determinate = s == CardState.Running && _percent >= 0;
        _bar.IsVisible  = active && determinate;     // thin progress line inside the header (only with a real percentage)
        _barDeterminate = determinate;
        if (determinate)
            SetBar(0, Math.Max(0.02, _percent));

        // Header, same behaviour as the generic tool widget: light sweep + pulsing dot while busy,
        // then a final ✓ / ✗ and the elapsed time.
        if (active)
        {
            // The shown time is the tool's own time: restart the clock when the AI has finished typing.
            if (s == CardState.Pending && prev != CardState.Pending && _live)
                _sw.Restart();

            _status.Text       = "●";
            _status.FontWeight = FontWeight.Normal;
            _statusBrush.Color = HdrAccent;
            _hdr.Background    = _live ? _sweep : _hdrFlat;
            _time.IsVisible    = _live;
        }
        else
        {
            _sw.Stop();
            _status.Opacity    = 1;
            _status.FontWeight = FontWeight.Bold;
            switch (s)
            {
                case CardState.Done:     _status.Text = "✓"; _statusBrush.Color = HdrOk;    break;
                case CardState.Failed:   _status.Text = "✗"; _statusBrush.Color = HdrErr;   break;
                default:                 _status.Text = "?"; _statusBrush.Color = HdrMuted; break;
            }
            _hdr.Background = _hdrFlat;

            double secs = _sw.Elapsed.TotalSeconds;
            if (_live && secs >= 0.05 && s != CardState.NoResult)
            {
                _time.Text      = FormatTime(secs);
                _time.IsVisible = true;
            }
            else
            {
                _time.IsVisible = false;
            }
        }

        UpdateTimer();
    }

    private void SetPill(IBrush bg, IBrush fg, string dot, string text)
    {
        _pill.Background     = bg;
        _pillDot.Foreground  = fg;
        _pillDot.Text        = dot;
        _pillText.Foreground = fg;
        _pillText.Text       = text;
    }

    private string Verb() => _kind switch
    {
        FileCardKind.Write     => "WRITING",
        FileCardKind.Append    => "APPENDING",
        FileCardKind.Edit      => "EDITING",
        FileCardKind.Insert    => "INSERTING",
        FileCardKind.Replace   => "REPLACING",
        FileCardKind.Remove    => "REMOVING",
        FileCardKind.Read      => "READING",
        FileCardKind.ReadLines => "READING",
        FileCardKind.Search    => "SEARCHING",
        FileCardKind.SearchDir => "SEARCHING",
        _                      => "WORKING",
    };

    private string DoneText() => _kind switch
    {
        FileCardKind.Write     => _existedBefore == true ? "OVERWRITTEN" : _existedBefore == false ? "CREATED" : "WRITTEN",
        FileCardKind.Append    => "APPENDED",
        FileCardKind.Edit      => "EDITED",
        FileCardKind.Insert    => "INSERTED",
        FileCardKind.Replace   => "REPLACED",
        FileCardKind.Remove    => "REMOVED",
        FileCardKind.Read      => "READ",
        FileCardKind.ReadLines => "READ",
        FileCardKind.Search    => "SEARCHED",
        FileCardKind.SearchDir => "SEARCHED",
        _                      => "DONE",
    };

    private void SetBar(double pos, double seg)
    {
        _barL.Width = new GridLength(Math.Max(0, pos), GridUnitType.Star);
        _barM.Width = new GridLength(Math.Max(0.0001, seg), GridUnitType.Star);
        _barR.Width = new GridLength(Math.Max(0, 1 - pos - seg), GridUnitType.Star);
    }

    private bool IsActive => _state == CardState.Streaming || _state == CardState.Pending || _state == CardState.Running;

    private void UpdateTimer()
    {
        if (_live && IsActive && _attached) _timer.Start();
        else                                _timer.Stop();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        // Header animation, same feel as the generic tool widget: pulsing dot, running time, light sweep.
        double t = _sw.Elapsed.TotalSeconds;
        double s = 0.5 + 0.5 * Math.Sin(t * 5.0);
        _status.Opacity = 0.35 + 0.65 * s;
        _time.Text      = FormatTime(t);

        double p = ((Environment.TickCount64 % 1800) / 1800.0) * 1.5 - 0.25;
        _g0.Offset = Math.Clamp(p - 0.22, 0, 1);
        _g1.Offset = Math.Clamp(p,        0, 1);
        _g2.Offset = Math.Clamp(p + 0.22, 0, 1);

        // Blinking caret at the end of the newest line.
        _tick++;
        if (_tick % 5 == 0)
        {
            _caretOn = !_caretOn;
            if (_caretRun != null)
                _caretRun.Text = _caretOn ? "▍" : " ";
        }
    }
    // ── Copy button ───────────────────────────────────────────────────────────

    /// <summary>Copies the full text (not just the folded preview) and flashes "✓ Copied".</summary>
    private async System.Threading.Tasks.Task CopyToClipboardAsync()
    {
        string text = _copyText;
        if (text.Length == 0) return;

        try
        {
            var clipboard = TopLevel.GetTopLevel(_root)?.Clipboard;
            if (clipboard == null) return;
            await clipboard.SetTextAsync(text);

            int flash = ++_copyFlash;
            _copyBtn.Content = "✓ Copied";
            DispatcherTimer.RunOnce(() =>
            {
                if (flash == _copyFlash) _copyBtn.Content = "⧉ Copy";
            }, TimeSpan.FromMilliseconds(1500));
        }
        catch
        {
            // Best effort — clipboard access can fail on some systems.
            _copyBtn.Content = "✕ Failed";
        }
    }


    // ── Small helpers ─────────────────────────────────────────────────────────

    // Light, readable colours on the dark card (the icon glyphs are single-colour).
    private static readonly SolidColorBrush IconRead   = new(Color.FromRgb(125, 185, 245));  // blue
    private static readonly SolidColorBrush IconSearch = new(Color.FromRgb(240, 200, 110));  // amber
    private static readonly SolidColorBrush IconWrite  = new(Color.FromRgb(130, 220, 160));  // green
    private static readonly SolidColorBrush IconEdit   = new(Color.FromRgb(190, 170, 245));  // violet
    private static readonly SolidColorBrush IconRemove = new(Color.FromRgb(240, 140, 140));  // red

    private static IBrush IconBrushFor(FileCardKind k) => k switch
    {
        FileCardKind.Read or FileCardKind.ReadLines     => IconRead,
        FileCardKind.Search or FileCardKind.SearchDir   => IconSearch,
        FileCardKind.Write or FileCardKind.Append
            or FileCardKind.Insert                      => IconWrite,
        FileCardKind.Remove                             => IconRemove,
        _                                               => IconEdit,
    };

    private static string IconFor(FileCardKind k) => k switch
    {
        FileCardKind.Write     => "📝",
        FileCardKind.Append    => "➕",
        FileCardKind.Edit      => "✏",
        FileCardKind.Insert    => "➕",
        FileCardKind.Replace   => "🔁",
        FileCardKind.Remove    => "🗑",
        FileCardKind.Read      => "📖",
        FileCardKind.ReadLines => "📖",
        FileCardKind.Search    => "🔍",
        FileCardKind.SearchDir => "🔍",
        _                      => "📄",
    };

    // Real tool names — the same wording as the generic tool widget (run_shell, browse_dir …).
    private static string TitleFor(FileCardKind k) => k switch
    {
        FileCardKind.Write     => "write_file",
        FileCardKind.Append    => "append_to_file",
        FileCardKind.Edit      => "edit_line",
        FileCardKind.Insert    => "insert_line",
        FileCardKind.Replace   => "replace_in_file",
        FileCardKind.Remove    => "remove_line",
        FileCardKind.Read      => "read_file",
        FileCardKind.ReadLines => "read_line",
        FileCardKind.Search    => "search_line",
        FileCardKind.SearchDir => "search_in_dir",
        _                      => "file",
    };

    private static string FormatTime(double seconds)
    {
        if (seconds < 60) return $"{seconds:F1}s";
        int m = (int)(seconds / 60);
        return $"{m}m {(int)(seconds - m * 60):D2}s";
    }

    private static bool IsReadKind(FileCardKind k) =>
        k == FileCardKind.Read || k == FileCardKind.ReadLines ||
        k == FileCardKind.Search || k == FileCardKind.SearchDir;
    private static string LineCountText(int n) => n == 1 ? "1 line" : $"{n} lines";

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)         return $"{bytes} B";
        if (bytes < 1024 * 1024)  return $"{bytes / 1024.0:0.#} KB";
        return $"{bytes / (1024.0 * 1024.0):0.#} MB";
    }

    private static string FirstLine(string text, int max)
    {
        int nl = text.IndexOf('\n');
        string line = (nl >= 0 ? text[..nl] : text).TrimEnd('\r').Trim();
        return line.Length > max ? line[..max] + "…" : line;
    }

    private static string? StrOf(Dictionary<string, PartialJson.Field> f, string key) =>
        f.TryGetValue(key, out var v) ? v.Value : null;

    private static int? IntOf(Dictionary<string, PartialJson.Field> f, string key)
    {
        if (f.TryGetValue(key, out var v) && v.Complete &&
            int.TryParse(v.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
            return n;
        return null;
    }

    /// <summary>"C:\Users\me\proj\a\b\c\File.cs" → "~/proj/…/c/File.cs" (user folder shown as ~).</summary>
    private static string ShortenPath(string path)
    {
        string p = path.Replace('\\', '/');
        try
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).Replace('\\', '/');
            if (!string.IsNullOrEmpty(home) && p.StartsWith(home, StringComparison.OrdinalIgnoreCase))
                p = "~" + p[home.Length..];
        }
        catch { /* ignore — show the full path */ }

        var segs = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segs.Length > 5)
            return segs[0] + "/" + segs[1] + "/…/" + segs[^2] + "/" + segs[^1];
        return string.Join("/", segs);
    }
}
