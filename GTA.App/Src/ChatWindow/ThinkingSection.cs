using System;
using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace GTA.App;

/// <summary>
/// The "Thinking / Thought" widget. Same header design as ToolCallSection (run_shell, browse_dir …):
/// status dot · icon + name · one-line gist · time · chevron, with a light sweep while the AI is thinking.
/// Folds itself (animated) when the thought is finished; the user can open / close it any time.
/// </summary>
internal sealed class ThinkingSection
{
    private const double LiveMaxHeight = 150;
    private const double OpenMaxHeight = 320;

    private static readonly Color Accent   = Color.FromRgb(170, 140, 235);
    private static readonly Color AccentHi = Color.FromRgb(205, 190, 255);
    private static readonly Color BaseBg   = Color.FromRgb(36, 31, 60);
    private static readonly Color HotBg    = Color.FromRgb(56, 46, 92);

    private readonly TextBlock       _tb;
    private readonly Border          _header;
    private readonly TextBlock       _status;
    private readonly SolidColorBrush _statusBrush;
    private readonly SolidColorBrush _labelBrush;
    private readonly SolidColorBrush _barBrush;
    private readonly SolidColorBrush _borderBrush;
    private readonly SolidColorBrush _flatBg;
    private readonly TextBlock       _label;
    private readonly TextBlock       _summary;
    private readonly TextBlock       _time;
    private readonly TextBlock       _chevron;
    private readonly Border          _body;
    private readonly ScrollViewer    _scroll;
    private readonly DispatcherTimer _pulse;
    private readonly Stopwatch       _sw = new();
    private readonly LinearGradientBrush _sweep;
    private readonly GradientStop _g0, _g1, _g2;

    private DispatcherTimer? _foldTimer;
    private bool _live;
    private bool _expanded;
    private bool _userToggled;
    private int  _lastLen;
    private int  _stickTicks;

    public Control Root { get; }

    public ThinkingSection(TextBlock thinkingTb, bool live)
    {
        _tb   = thinkingTb;
        _live = live;

        // ── Text style (compact, calm) ───────────────────────────────────────
        _tb.FontSize     = 11.5;
        _tb.LineHeight   = 17;
        _tb.Opacity      = 1;
        _tb.Foreground   = new SolidColorBrush(Color.FromRgb(150, 145, 178));
        _tb.TextWrapping = TextWrapping.Wrap;
        _tb.Margin       = new Thickness(0, 0, 10, 0);

        // ── Header cells (same layout as ToolCallSection) ────────────────────
        _statusBrush = new SolidColorBrush(Accent);
        _status = new TextBlock
        {
            Text              = live ? "●" : "✓",
            FontSize          = 10,
            Foreground        = _statusBrush,
            Width             = 14,
            TextAlignment     = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity           = live ? 1 : 0.75,
        };

        _labelBrush = new SolidColorBrush(Accent);
        _label = new TextBlock
        {
            Text              = live ? "Thinking" : "Thought",
            FontSize          = 11,
            FontWeight        = FontWeight.SemiBold,
            Foreground        = _labelBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var nameRow = new StackPanel
        {
            Orientation       = Orientation.Horizontal,
            Spacing           = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        nameRow.Children.Add(new TextBlock
        {
            Text              = "🧠",
            FontSize          = 11,
            Foreground        = new SolidColorBrush(Accent),   // mono glyph fallback would be black on the dark header
            VerticalAlignment = VerticalAlignment.Center,
        });
        nameRow.Children.Add(_label);

        _summary = new TextBlock
        {
            Text              = live ? "" : Snippet(_tb.Text, tail: false),
            FontSize          = 11,
            FontFamily        = new FontFamily("Consolas, Courier New, monospace"),
            Foreground        = LlmChatWindow.ColMuted,
            Opacity           = 0.8,
            FontStyle         = FontStyle.Italic,
            TextTrimming      = TextTrimming.CharacterEllipsis,
            MaxLines          = 1,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(12, 0, 10, 0),
        };

        _time = new TextBlock
        {
            Text              = "",
            FontSize          = 10,
            FontFamily        = new FontFamily("Consolas, Courier New, monospace"),
            Foreground        = LlmChatWindow.ColMuted,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(0, 0, 8, 0),
            IsVisible         = false,
        };

        _chevron = new TextBlock
        {
            Text              = live ? "▾" : "▸",
            FontSize          = 10,
            Foreground        = LlmChatWindow.ColMuted,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,Auto") };
        Grid.SetColumn(_status,  0);
        Grid.SetColumn(nameRow,  1);
        Grid.SetColumn(_summary, 2);
        Grid.SetColumn(_time,    3);
        Grid.SetColumn(_chevron, 4);
        grid.Children.Add(_status);
        grid.Children.Add(nameRow);
        grid.Children.Add(_summary);
        grid.Children.Add(_time);
        grid.Children.Add(_chevron);

        // Soft light that sweeps across the header while the AI is thinking.
        _g0 = new GradientStop { Color = BaseBg, Offset = 0.0 };
        _g1 = new GradientStop { Color = HotBg,  Offset = 0.5 };
        _g2 = new GradientStop { Color = BaseBg, Offset = 1.0 };
        _sweep = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint   = new RelativePoint(1, 0.5, RelativeUnit.Relative),
        };
        _sweep.GradientStops.Add(_g0);
        _sweep.GradientStops.Add(_g1);
        _sweep.GradientStops.Add(_g2);

        _flatBg      = new SolidColorBrush(BaseBg);
        _borderBrush = new SolidColorBrush(Color.FromArgb(70, Accent.R, Accent.G, Accent.B));

        _header = new Border
        {
            Background          = live ? _sweep : _flatBg,
            BorderBrush         = _borderBrush,
            BorderThickness     = new Thickness(1),
            CornerRadius        = new CornerRadius(8),
            Padding             = new Thickness(8, 3, 10, 3),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Cursor              = new Cursor(StandardCursorType.Hand),
            Child               = grid,
        };
        ToolTip.SetTip(_header, "Click to expand / collapse");
        _header.PointerEntered += (_, _) => _borderBrush.Color = Color.FromArgb(150, Accent.R, Accent.G, Accent.B);
        _header.PointerExited  += (_, _) => _borderBrush.Color = Color.FromArgb(70,  Accent.R, Accent.G, Accent.B);
        _header.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left)
                Toggle();
        };

        // ── Body: left accent bar + small scroll window ──────────────────────
        _scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility   = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            MaxHeight                     = live ? LiveMaxHeight : OpenMaxHeight,
            Content                       = _tb,
        };

        _barBrush = new SolidColorBrush(live
            ? Color.FromArgb(200, Accent.R, Accent.G, Accent.B)
            : Color.FromArgb(90,  Accent.R, Accent.G, Accent.B));

        _body = new Border
        {
            IsVisible       = live,
            BorderBrush     = _barBrush,
            BorderThickness = new Thickness(2, 0, 0, 0),
            Padding         = new Thickness(10, 2, 2, 2),
            Margin          = new Thickness(10, 3, 0, 2),
            ClipToBounds    = true,   // nothing spills out while the fold animation shrinks it
            Child           = live ? _scroll : null,   // a replayed thought is folded: its text view is attached on the first open
        };
        _expanded = live;

        var root = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin              = new Thickness(4, 1, 4, 1),
        };
        root.Children.Add(_header);
        root.Children.Add(_body);
        Root = root;

        // ── Live animation (pulse + sweep + timer + tail-scroll) ─────────────
        _pulse = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _pulse.Tick += (_, _) => OnPulse();

        if (live)
        {
            _sw.Start();
            Root.AttachedToVisualTree   += (_, _) => { if (_live) _pulse.Start(); };
            Root.DetachedFromVisualTree += (_, _) => _pulse.Stop();
        }
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Thinking block finished streaming: stop animation and auto-fold.</summary>
    public void Finish()
    {
        if (!_live) return;
        _live = false;
        _sw.Stop();
        _pulse.Stop();

        double secs = _sw.Elapsed.TotalSeconds;
        _label.Text       = "Thought";
        _labelBrush.Color = Accent;
        _status.Text      = "✓";
        _status.Opacity   = 0.8;
        _statusBrush.Color = Accent;
        _header.Background = _flatBg;
        _barBrush.Color   = Color.FromArgb(90, Accent.R, Accent.G, Accent.B);
        _summary.Text     = Snippet(_tb.Text, tail: false);
        if (secs >= 0.1)
        {
            _time.Text      = FormatTime(secs);
            _time.IsVisible = true;
        }

        if (!_userToggled)
        {
            FoldAnimated();
        }
        else
        {
            // User already decided open/closed. Respect it, just allow a taller window.
            _scroll.MaxHeight = OpenMaxHeight;
        }
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    private void OnPulse()
    {
        double t = _sw.Elapsed.TotalSeconds;
        double s = 0.5 + 0.5 * Math.Sin(t * 5.0);

        _status.Opacity   = 0.35 + 0.65 * s;
        _labelBrush.Color = Lerp(Accent, AccentHi, s);
        _time.Text        = FormatTime(t);
        _time.IsVisible   = true;

        // light sweep: position runs from -0.25 to 1.25 every 1.8 s
        double p = ((Environment.TickCount64 % 1800) / 1800.0) * 1.5 - 0.25;
        _g0.Offset = Math.Clamp(p - 0.22, 0, 1);
        _g1.Offset = Math.Clamp(p,        0, 1);
        _g2.Offset = Math.Clamp(p + 0.22, 0, 1);

        int len = _tb.Text?.Length ?? 0;
        if (len != _lastLen)
        {
            _lastLen    = len;
            _stickTicks = 3;
            _summary.Text = Snippet(_tb.Text, tail: true);   // live gist = the newest words
        }

        // Keep the newest lines visible while text is still growing.
        if (_expanded && _body.IsVisible && _stickTicks > 0)
        {
            _stickTicks--;
            _scroll.ScrollToEnd();
        }
    }

    private void Toggle()
    {
        _userToggled = true;
        if (_expanded)
        {
            FoldAnimated();
        }
        else
        {
            CancelFold();
            _expanded         = true;
            _scroll.MaxHeight = _live ? LiveMaxHeight : OpenMaxHeight;
            if (_body.Child == null) _body.Child = _scroll;   // replayed / released thought: put the text view back
            _chevron.Text     = "▾";
            if (_live) _stickTicks = 3;
            _foldTimer = FoldAnimator.Open(_body);
        }
    }

    private void CancelFold()
    {
        _foldTimer?.Stop();
        _foldTimer = null;
    }

    private void FoldAnimated()
    {
        _expanded     = false;
        _chevron.Text = "▸";
        CancelFold();

        DispatcherTimer? timer = null;
        timer = FoldAnimator.Close(_body, () =>
        {
            _scroll.MaxHeight = _live ? LiveMaxHeight : OpenMaxHeight;
            // MEMORY: a finished, folded thought keeps no ScrollViewer (templated, ~30 controls) in the visual tree.
            if (!_live && !_expanded) _body.Child = null;
            if (ReferenceEquals(_foldTimer, timer)) _foldTimer = null;
        });
        _foldTimer = timer;
    }

    /// <summary>One-line gist of the thought: the newest words while live, the opening words when done.</summary>
    private static string Snippet(string? text, bool tail, int max = 140)
    {
        if (string.IsNullOrWhiteSpace(text) || text == "…") return string.Empty;

        int scan = max * 3;
        string src = tail
            ? text.Substring(Math.Max(0, text.Length - scan))
            : text.Substring(0, Math.Min(text.Length, scan));

        var sb = new StringBuilder(src.Length);
        bool lastSpace = false;
        foreach (char ch in src)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastSpace) sb.Append(' ');
                lastSpace = true;
            }
            else
            {
                sb.Append(ch);
                lastSpace = false;
            }
        }

        string s = sb.ToString().Trim();
        if (s.Length > max)
            s = tail ? "…" + s[^max..] : s[..max] + "…";
        return s;
    }

    private static string FormatTime(double seconds)
    {
        if (seconds < 60) return $"{seconds:F1}s";
        int m = (int)(seconds / 60);
        return $"{m}m {(int)(seconds - m * 60):D2}s";
    }

    private static Color Lerp(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t));
}
