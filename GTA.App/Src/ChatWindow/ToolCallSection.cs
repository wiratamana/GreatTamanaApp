using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GTA.Core;

namespace GTA.App;

internal sealed class ToolCallSection
{
    private static readonly FontFamily Mono = new("Consolas, Courier New, monospace");

    private readonly bool    _isDispatch;
    private readonly Color   _accent;
    private readonly Color   _baseBg;
    private readonly Color   _hotBg;
    private readonly Action<bool>? _onToggle;

    private readonly Border          _header;
    private readonly SolidColorBrush _borderBrush;
    private readonly TextBlock       _status;
    private readonly TextBlock       _summary;
    private readonly TextBlock       _time;
    private readonly TextBlock       _chevron;
    private readonly Border          _body;
    private readonly DispatcherTimer _pulse;
    private readonly Stopwatch       _sw = new();
    private readonly LinearGradientBrush _sweep;
    private readonly GradientStop _g0, _g1, _g2;

    private DispatcherTimer? _foldTimer;
    private Action? _pendingAfterFold;
    private bool _live;
    private bool _running;
    private bool _expanded;
    private bool _userToggled;

    // MEMORY: the body (PARAMS grid + OUTPUT, each with ScrollViewers / templated controls) is the heavy part of a
    // tool card. A folded, finished card does not keep it: it is built from this factory when the user opens the
    // card, and dropped again when the card is folded.
    private Func<Control>? _bodyFactory;

    // Replay of a file tool (write_file, edit_line ...): the cheap header stands in for the heavy FileToolCard,
    // which is only built (and swapped in) when the user clicks the header.
    private Func<(Control Real, Action Expand)>? _swapFactory;

    public Control Root { get; }
    public bool IsExpanded => _expanded;

    /// <summary>Name of the tool this section shows.</summary>
    public string ToolName { get; }

    /// <summary>The parameters (JSON) given in SetParams — needed to rebuild a released body.</summary>
    public string? InputJson { get; private set; }

    /// <summary>True when Finish() will fold the body by itself (live call the user did not touch).</summary>
    public bool WillAutoFold => _live && !_userToggled && _expanded;

    /// <summary>Replay card with a LAZY body: <paramref name="bodyFactory"/> runs on the first open (see class comment).</summary>
    public ToolCallSection(string toolName, Func<Control> bodyFactory, Action<bool>? onToggle, bool live)
        : this(toolName, (Control?)null, onToggle, live)
    {
        _bodyFactory = bodyFactory;
    }

    /// <summary>Replay card that is only a header; a click builds the real widget and swaps it in (see _swapFactory).</summary>
    public ToolCallSection(string toolName, Func<(Control Real, Action Expand)> swapFactory)
        : this(toolName, (Control?)null, null, live: false)
    {
        _swapFactory = swapFactory;
    }

    public ToolCallSection(string toolName, Control? bodyContent, Action<bool>? onToggle, bool live)
    {
        ToolName    = toolName;
        _isDispatch = toolName == "dispatch_sub_agent";
        _onToggle   = onToggle;
        _live       = live;
        _expanded   = live;

        _accent = _isDispatch ? Color.FromRgb(170, 140, 235) : Color.FromRgb(115, 165, 225);
        _baseBg = _isDispatch ? Color.FromRgb(36, 31, 60)    : Color.FromRgb(27, 31, 46);
        _hotBg  = _isDispatch ? Color.FromRgb(56, 46, 92)    : Color.FromRgb(38, 50, 78);

        // ── Header cells ─────────────────────────────────────────────────────
        _status = new TextBlock
        {
            Text                = "●",
            FontSize            = 10,
            Foreground          = new SolidColorBrush(_accent),
            Width               = 14,
            TextAlignment       = TextAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
        };

        var nameRow = new StackPanel
        {
            Orientation       = Orientation.Horizontal,
            Spacing           = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        nameRow.Children.Add(new TextBlock
        {
            Text = _isDispatch ? "🤖" : "🔧", FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(_accent),   // mono glyph fallback would be black on the dark header
        });
        nameRow.Children.Add(new TextBlock
        {
            Text              = _isDispatch ? "Sub Agent" : toolName,
            FontSize          = 11,
            FontFamily        = _isDispatch ? FontFamily.Default : Mono,
            FontWeight        = FontWeight.SemiBold,
            Foreground        = new SolidColorBrush(_accent),
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (_isDispatch)
            nameRow.Children.Add(new TextBlock
            {
                Text              = "dispatch_sub_agent",
                FontSize          = 10,
                FontFamily        = Mono,
                Foreground        = LlmChatWindow.ColMuted,
                Opacity           = 0.8,
                VerticalAlignment = VerticalAlignment.Center,
            });

        _summary = new TextBlock
        {
            Text              = live ? "preparing…" : "",
            FontSize          = 11,
            FontFamily        = Mono,
            Foreground        = LlmChatWindow.ColMuted,
            Opacity           = 0.9,
            TextTrimming      = TextTrimming.CharacterEllipsis,
            MaxLines          = 1,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(12, 0, 10, 0),
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

        // Soft light that sweeps across the header while the tool is running.
        _g0 = new GradientStop { Color = _baseBg, Offset = 0.0 };
        _g1 = new GradientStop { Color = _hotBg,  Offset = 0.5 };
        _g2 = new GradientStop { Color = _baseBg, Offset = 1.0 };
        _sweep = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint   = new RelativePoint(1, 0.5, RelativeUnit.Relative),
        };
        _sweep.GradientStops.Add(_g0);
        _sweep.GradientStops.Add(_g1);
        _sweep.GradientStops.Add(_g2);

        _borderBrush = new SolidColorBrush(Color.FromArgb(70, _accent.R, _accent.G, _accent.B));

        _header = new Border
        {
            Background          = live ? _sweep : new SolidColorBrush(_baseBg),
            BorderBrush         = _borderBrush,
            BorderThickness     = new Thickness(1),
            CornerRadius        = new CornerRadius(8),
            Padding             = new Thickness(8, 3, 10, 3),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Cursor              = new Cursor(StandardCursorType.Hand),
            Child               = grid,
        };
        ToolTip.SetTip(_header, "Click to expand / collapse parameters and output");
        _header.PointerEntered += (_, _) => _borderBrush.Color = Color.FromArgb(150, _accent.R, _accent.G, _accent.B);
        _header.PointerExited  += (_, _) => _borderBrush.Color = Color.FromArgb(70,  _accent.R, _accent.G, _accent.B);
        _header.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left) Toggle();
        };

        // ── Body ─────────────────────────────────────────────────────────────
        _body = new Border
        {
            IsVisible           = live,
            Background          = LlmChatWindow.BgCodeBlock,
            BorderBrush         = LlmChatWindow.ColDivider,
            BorderThickness     = new Thickness(1),
            CornerRadius        = new CornerRadius(6),
            Padding             = new Thickness(12, 8, 12, 8),
            Margin              = new Thickness(0, 3, 0, 4),
            ClipToBounds        = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child               = bodyContent,
        };

        var root = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin              = new Thickness(4, 1, 4, 1),
        };
        root.Children.Add(_header);
        root.Children.Add(_body);
        Root = root;

        // ── Live animation ───────────────────────────────────────────────────
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

    /// <summary>The call's final parameters are known: show the one-line summary; a live call is now "running".</summary>
    public void SetParams(string? inputJson)
    {
        InputJson = inputJson;
        _summary.Text = PickSummary(inputJson) ?? "";
        if (_live)
        {
            _running = true;
            _sw.Restart();
            _time.Text      = "0.0s";
            _time.IsVisible = true;
        }
    }

    /// <summary>A live call finished.</summary>
    public void Finish(string? result, Action? afterFold)
    {
        if (!_live) { afterFold?.Invoke(); return; }

        bool fold = WillAutoFold;
        _live = false;
        _sw.Stop();
        _pulse.Stop();
        ShowDone(result, _running ? _sw.Elapsed : null);
        _running = false;

        if (fold)
        {
            FoldAnimated(afterFold);
        }
        else
        {
            afterFold?.Invoke();
        }
    }

    /// <summary>Reloaded session: show the saved outcome.</summary>
    public void MarkStatic(string? result, TimeSpan? duration) => ShowDone(result, duration);

    // ── Internals ─────────────────────────────────────────────────────────────

    private void ShowDone(string? result, TimeSpan? duration)
    {
        bool error = IsErrorResult(result);
        var col = error ? Color.FromRgb(235, 105, 105) : Color.FromRgb(100, 200, 140);

        _status.Text       = error ? "✗" : "✓";
        _status.Opacity    = 1;
        _status.FontWeight = FontWeight.Bold;
        _status.Foreground = new SolidColorBrush(col);

        _header.Background = new SolidColorBrush(_baseBg);

        if (duration.HasValue && duration.Value.TotalSeconds >= 0.05)
        {
            _time.Text      = FormatTime(duration.Value.TotalSeconds);
            _time.IsVisible = true;
        }
        else if (!_running)
        {
            _time.IsVisible = false;
        }
    }

    private void OnPulse()
    {
        double t = _sw.Elapsed.TotalSeconds;
        double s = 0.5 + 0.5 * Math.Sin(t * 5.0);
        _status.Opacity = 0.35 + 0.65 * s;

        if (_running)
            _time.Text = FormatTime(t);

        // light sweep: position runs from -0.25 to 1.25 every 1.8 s
        double p = ((Environment.TickCount64 % 1800) / 1800.0) * 1.5 - 0.25;
        _g0.Offset = Math.Clamp(p - 0.22, 0, 1);
        _g1.Offset = Math.Clamp(p,        0, 1);
        _g2.Offset = Math.Clamp(p + 0.22, 0, 1);
    }

    private void Toggle()
    {
        // Replay of a file tool: build the real (heavy) card now and put it where this header is.
        if (_swapFactory != null)
        {
            var make = _swapFactory;
            _swapFactory = null;
            if (Root.Parent is Panel host)
            {
                int at = host.Children.IndexOf(Root);
                if (at >= 0)
                {
                    var (real, expand) = make();
                    host.Children[at] = real;
                    expand();
                    return;
                }
            }
            return;
        }

        _userToggled = true;

        if (_expanded)
        {
            FoldAnimated(null);
        }
        else
        {
            CancelFold();   // may run a pending afterFold → result is stored before we expand
            _expanded     = true;
            _chevron.Text = "▾";
            EnsureBody();              // lazy card: build the PARAMS / OUTPUT body now
            _onToggle?.Invoke(true);   // builds the (lazy) output first, so the open animation knows the real height
            _foldTimer = FoldAnimator.Open(_body);
        }
    }

    private void CancelFold()
    {
        _foldTimer?.Stop();
        _foldTimer = null;

        var pending = _pendingAfterFold;
        _pendingAfterFold = null;
        pending?.Invoke();
    }

    private void FoldAnimated(Action? afterFold)
    {
        _expanded     = false;
        _chevron.Text = "▸";
        CancelFold();
        _pendingAfterFold = afterFold;

        double start = _body.Bounds.Height;
        if (!_body.IsVisible || start < 4)
        {
            _body.IsVisible = false;
            FinishFold();
            return;
        }

        _body.MaxHeight = start;
        var sw    = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _foldTimer = timer;
        timer.Tick += (_, _) =>
        {
            double p = Math.Min(1.0, sw.Elapsed.TotalMilliseconds / 220.0);
            double e = 1.0 - Math.Pow(1.0 - p, 3);
            _body.MaxHeight = Math.Max(0, start * (1.0 - e));
            _body.Opacity   = 1.0 - e;
            if (p >= 1.0)
            {
                timer.Stop();
                if (ReferenceEquals(_foldTimer, timer)) _foldTimer = null;
                _body.IsVisible = false;
                _body.Opacity   = 1;
                _body.MaxHeight = double.PositiveInfinity;
                FinishFold();
            }
        };
        timer.Start();
    }

    private void FinishFold()
    {
        var pending = _pendingAfterFold;
        _pendingAfterFold = null;
        pending?.Invoke();
        _onToggle?.Invoke(false);
        ReleaseBody();
    }

    /// <summary>Let the factory-built body go (only for a finished card that can rebuild it on demand).</summary>
    private void ReleaseBody()
    {
        if (_bodyFactory == null || _live || _expanded) return;
        _body.Child = null;
    }

    private void EnsureBody()
    {
        if (_bodyFactory != null && _body.Child == null)
            _body.Child = _bodyFactory();
    }

    /// <summary>
    /// A live card has just finished: from now on its body can be rebuilt from <paramref name="factory"/>, so a
    /// folded card keeps only its header. If the card is open right now, the body is released when it is folded.
    /// </summary>
    public void MakeLazy(Func<Control> factory)
    {
        _bodyFactory = factory;
        ReleaseBody();
    }

    private static string FormatTime(double seconds)
    {
        if (seconds < 60) return $"{seconds:F1}s";
        int m = (int)(seconds / 60);
        return $"{m}m {(int)(seconds - m * 60):D2}s";
    }

    private static bool IsErrorResult(string? r)
    {
        if (string.IsNullOrEmpty(r)) return false;
        string t = r.TrimStart();
        return t.StartsWith("Error", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("[Error", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("⚠")
            || t.StartsWith("✗")
            || t.StartsWith("❌");
    }

    // ── Body layout (PARAMS / OUTPUT) ─────────────────────────────────────────

    /// <summary>The body: a PARAMS block and an OUTPUT block with small coloured captions.</summary>
    public static StackPanel BuildBody(Control paramsContent, Control outputContent, bool isDispatch)
    {
        var accent = isDispatch ? Color.FromRgb(170, 140, 235) : Color.FromRgb(115, 165, 225);

        TextBlock Caption(string text) => new TextBlock
        {
            Text       = text,
            FontSize   = 9,
            FontFamily = Mono,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.FromArgb(190, accent.R, accent.G, accent.B)),
            Margin     = new Thickness(0, 0, 0, 2),
        };

        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(Caption("PARAMS"));
        panel.Children.Add(paramsContent);
        panel.Children.Add(new Border
        {
            Height     = 1,
            Background = LlmChatWindow.ColDivider,
            Opacity    = 0.5,
            Margin     = new Thickness(0, 6, 0, 6),
        });
        panel.Children.Add(Caption("OUTPUT"));
        panel.Children.Add(outputContent);
        return panel;
    }

    // ── Human-readable parameters (display only; the real JSON is untouched) ──

    private static readonly string[] SummaryKeys =
    {
        "command", "path", "file_path", "url", "query", "pattern", "name", "title",
        "key", "ticket_key", "issue_key", "tool_names", "text", "message", "task", "code", "content",
    };

    /// <summary>One-line summary for the header: the most telling parameter value.</summary>
    internal static string? PickSummary(string? inputJson)
    {
        if (string.IsNullOrWhiteSpace(inputJson)) return null;
        try
        {
            if (JsonNode.Parse(inputJson) is not JsonObject obj || obj.Count == 0) return "(no parameters)";

            foreach (string k in SummaryKeys)
                if (obj.TryGetPropertyValue(k, out var v) && v != null)
                    return OneLine(ScalarToText(v));

            foreach (var kv in obj)
                if (kv.Value != null)
                    return OneLine(kv.Key.Replace('_', ' ') + ": " + ScalarToText(kv.Value));
        }
        catch { /* partial / invalid JSON → no summary */ }
        return null;
    }

    private static string OneLine(string s)
    {
        var sb = new StringBuilder(Math.Min(s.Length, 300));
        bool lastSpace = false;
        foreach (char ch in s)
        {
            if (sb.Length >= 300) break;
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
        return sb.ToString().Trim();
    }

    private static string ScalarToText(JsonNode v)
    {
        if (v is JsonValue jv)
        {
            if (jv.TryGetValue<string>(out var s)) return s;
            return jv.ToJsonString();
        }
        if (v is JsonArray arr)
        {
            var parts = new List<string>();
            foreach (var item in arr) parts.Add(item == null ? "null" : ScalarToText(item));
            return string.Join(", ", parts);
        }
        return v.ToJsonString();
    }

    /// <summary>Parameters as clean "name value" rows.</summary>
    public static Control BuildReadableParams(string? inputJson, string? toolName = null)
    {
        if (string.IsNullOrWhiteSpace(inputJson))
            return MakeText("(no parameters)", Color.FromRgb(130, 130, 155), italic: true);

        JsonNode? node;
        try { node = JsonNode.Parse(inputJson); }
        catch { return MakeText(inputJson, Color.FromRgb(160, 200, 240)); }

        if (node is not JsonObject obj)
            return MakeText(JsonUtils.TryPrettyPrintJson(inputJson), Color.FromRgb(160, 200, 240));
        if (obj.Count == 0)
            return MakeText("(no parameters)", Color.FromRgb(130, 130, 155), italic: true);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        int row = 0;
        foreach (var kv in obj)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            var key = new TextBlock
            {
                Text              = kv.Key.Replace('_', ' '),
                FontSize          = 11,
                FontFamily        = Mono,
                Foreground        = LlmChatWindow.ColMuted,
                VerticalAlignment = VerticalAlignment.Top,
                Margin            = new Thickness(0, 0, 14, 3),
            };
            Grid.SetRow(key, row);
            Grid.SetColumn(key, 0);
            grid.Children.Add(key);

            var value = BuildValue(kv.Key, kv.Value, toolName);
            value.Margin = new Thickness(0, 0, 0, 3);
            Grid.SetRow(value, row);
            Grid.SetColumn(value, 1);
            grid.Children.Add(value);

            row++;
        }

        return new ScrollViewer
        {
            MaxHeight                     = 220,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility   = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content                       = grid,
        };
    }

    private const int MaxValueChars = 1500;

    // Python code is coloured in full; this is the most we colour (very long code is cut).
    private const int MaxPythonChars = 12000;

    /// <summary>The Python source from inline_python's "code" parameter, with syntax colours.</summary>
    private static Control BuildPythonValue(string code)
    {
        var tb = new SelectableTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize     = 11.5,
            LineHeight   = 16,
            FontFamily   = Mono,
            Foreground   = new SolidColorBrush(Color.FromRgb(190, 196, 210)),
        };

        string shown = code.Length > MaxPythonChars ? code.Substring(0, MaxPythonChars) : code;
        var inlines = tb.Inlines;
        if (inlines == null)
        {
            tb.Text = shown;   // no inline support → plain text (never expected)
            return tb;
        }

        try
        {
            PythonHighlighter.Append(inlines, shown);
        }
        catch
        {
            // Colouring must never break the display: fall back to plain text.
            inlines.Clear();
            tb.Text = shown;
            return tb;
        }

        if (code.Length > MaxPythonChars)
        {
            inlines.Add(new LineBreak());
            inlines.Add(new Run($"… (+{code.Length - MaxPythonChars:N0} more characters)")
            {
                Foreground = LlmChatWindow.ColMuted,
                FontStyle  = FontStyle.Italic,
            });
        }
        return tb;
    }

    private static Control BuildValue(string key, JsonNode? v, string? toolName = null)
    {
        // inline_python → "code" gets Python syntax highlighting.
        if (toolName == "inline_python" && key == "code"
            && v is JsonValue pyVal && pyVal.TryGetValue<string>(out var pyCode)
            && !string.IsNullOrEmpty(pyCode))
        {
            return BuildPythonValue(pyCode);
        }

        string text;
        Color  color = Color.FromRgb(205, 214, 232);   // normal value
        bool   italic = false;

        switch (v)
        {
            case null:
                text = "null"; color = Color.FromRgb(130, 130, 155); italic = true;
                break;

            case JsonValue jv when jv.TryGetValue<string>(out var s):
                string tr = s.TrimStart();
                text = (tr.Length > 0 && (tr[0] == '{' || tr[0] == '['))
                    ? JsonUtils.TryPrettyPrintJson(s)      // stringified JSON → readable
                    : s;
                if (key is "command" or "code" or "script" or "cmd") color = Color.FromRgb(180, 210, 255);
                break;

            case JsonValue jv2:
                text  = jv2.ToJsonString();                 // number / bool
                color = text is "true" or "false" ? Color.FromRgb(230, 170, 110) : Color.FromRgb(140, 200, 240);
                break;

            case JsonArray arr:
                bool allScalar = true;
                foreach (var item in arr)
                    if (item is JsonObject or JsonArray) { allScalar = false; break; }
                if (allScalar)
                {
                    var lines = new List<string>();
                    foreach (var item in arr) lines.Add(item == null ? "null" : ScalarToText(item));
                    text = arr.Count <= 3 && string.Join(", ", lines).Length < 120
                        ? string.Join(", ", lines)
                        : string.Join("\n", lines.ConvertAll(l => "• " + l));
                }
                else
                {
                    text = JsonUtils.TryPrettyPrintJson(arr.ToJsonString());
                }
                break;

            default:
                text = JsonUtils.TryPrettyPrintJson(v.ToJsonString());
                break;
        }

        if (text.Length > MaxValueChars)
            text = text.Substring(0, MaxValueChars) + $"\n… (+{text.Length - MaxValueChars:N0} more characters)";

        return MakeText(text, color, italic);
    }

    private static SelectableTextBlock MakeText(string text, Color color, bool italic = false) => new()
    {
        Text         = text,
        TextWrapping = TextWrapping.Wrap,
        FontSize     = 11.5,
        LineHeight   = 16,
        FontFamily   = Mono,
        FontStyle    = italic ? FontStyle.Italic : FontStyle.Normal,
        Foreground   = new SolidColorBrush(color),
    };
}
