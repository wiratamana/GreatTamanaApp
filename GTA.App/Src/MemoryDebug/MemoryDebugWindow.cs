#if !CO_WORKER_USE
using System;
using System.Diagnostics;
using System.Runtime;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace GTA.App;

/// <summary>
/// Developer-only "🧠 Memory" window (opened from the header, left of 💉 Inject).
///   - live section: OS counters, GC heap, generations, JIT, thread pool, modules (refresh / auto-refresh)
///   - heap analysis: writes a TEMPORARY full dump of this process, counts objects per type, DELETES the dump.
/// Not part of the CO_WORKER_USE build.
/// </summary>
internal sealed class MemoryDebugWindow : Window
{
    private static readonly FontFamily Mono = new("Consolas, Courier New, monospace");
    private static readonly SolidColorBrush ColWarn = new(Color.FromRgb(224, 160, 80));

    private readonly SelectableTextBlock _live;
    private readonly SelectableTextBlock _heap;
    private readonly StackPanel          _summary;
    private readonly TextBlock           _status;
    private readonly Button              _analyzeBtn;
    private readonly CheckBox            _autoChk;
    private readonly CheckBox            _gcFirstChk;
    private readonly DispatcherTimer     _timer;

    public MemoryDebugWindow()
    {
        Title                 = "🧠 Memory debug";
        Width                 = 960;
        Height                = 760;
        MinWidth              = 640;
        MinHeight             = 400;
        Background            = LlmChatWindow.BgMain;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        // ── Buttons ──────────────────────────────────────────────────────────
        var refreshBtn = MakeButton("⟳ Refresh", LlmChatWindow.BgAiBubble, LlmChatWindow.ColText, true);
        refreshBtn.Click += (_, _) => RefreshLive();

        var gcBtn = MakeButton("🧹 Force GC", LlmChatWindow.BgAiBubble, LlmChatWindow.ColText, true);
        ToolTip.SetTip(gcBtn, "Blocking, compacting GC of all generations + LOH. Then refreshes the numbers.");
        gcBtn.Click += (_, _) =>
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            RefreshLive();
        };

        var copyBtn = MakeButton("📋 Copy all", LlmChatWindow.BgAiBubble, LlmChatWindow.ColText, true);
        copyBtn.Click += async (_, _) =>
        {
            var clip = GetTopLevel(this)?.Clipboard;
            if (clip == null) return;
            await clip.SetTextAsync((_live.Text ?? "") + "\n\n" + (_heap.Text ?? ""));
            _status.Text = "Copied to clipboard.";
        };

        _analyzeBtn = MakeButton("🔬 Analyze heap", LlmChatWindow.ColAccent, Brushes.White, false);
        ToolTip.SetTip(_analyzeBtn,
            "Writes a TEMPORARY full dump of this app to the temp folder, sorts every object into groups " +
            "(strings, Avalonia, images, raw bytes, GTA, other), then deletes the dump file. Nothing is kept on disk.");
        _analyzeBtn.Click += async (_, _) => await AnalyzeAsync();

        _gcFirstChk = new CheckBox { Content = "GC first (live objects only)", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        UiTheme.StyleCheckBox(_gcFirstChk, LlmChatWindow.ColText);
        ToolTip.SetTip(_gcFirstChk, "Run a full GC before the dump, so garbage is not counted.");

        _autoChk = new CheckBox { Content = "Auto refresh (2 s)", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        UiTheme.StyleCheckBox(_autoChk, LlmChatWindow.ColText);

        var buttonRow = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        foreach (Control c in new Control[] { refreshBtn, _autoChk, gcBtn, copyBtn, _analyzeBtn, _gcFirstChk })
        {
            c.Margin = new Thickness(0, 0, 10, 6);
            buttonRow.Children.Add(c);
        }

        _status = new TextBlock
        {
            FontSize     = 11,
            Foreground   = LlmChatWindow.ColMuted,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 0, 0, 8),
            Text         = "Dump files are temporary: written to the temp folder and deleted right after the analysis.",
        };

        // ── Sections: [where-is-the-memory bars] [live numbers] [heap report] ─
        _summary = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        _live = MakeText();
        _heap = MakeText();
        _heap.Margin = new Thickness(0, 14, 0, 0);
        _heap.Text   = "HEAP ANALYSIS: not run yet. Press \"Analyze heap\" to sort the managed heap into " +
                       "Strings / Avalonia objects / Images / Raw bytes / GTA objects / Other.";

        var body = new StackPanel();
        body.Children.Add(_summary);
        body.Children.Add(_live);
        body.Children.Add(_heap);

        var scroll = new ScrollViewer
        {
            Content                       = body,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility   = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };

        var top = new StackPanel { Margin = new Thickness(16, 14, 16, 0) };
        top.Children.Add(buttonRow);
        top.Children.Add(_status);

        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(new Border { Padding = new Thickness(16, 0, 16, 14), Child = scroll });
        Content = root;

        // ── Wiring ───────────────────────────────────────────────────────────
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => RefreshLive();
        _autoChk.IsCheckedChanged += (_, _) =>
        {
            if (_autoChk.IsChecked == true) _timer.Start(); else _timer.Stop();
        };

        KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
        Closed  += (_, _) => { _timer.Stop(); MemoryDumpAnalyzer.CleanupStale(); };

        // Remove dump files that an earlier crash may have left behind.
        MemoryDumpAnalyzer.CleanupStale();
        RefreshLive();
    }

    private static Button MakeButton(string text, IBrush bg, IBrush fg, bool bordered)
    {
        var b = new Button { Content = text, FontSize = 12, Padding = new Thickness(12, 6, 12, 6) };
        if (bordered)
            UiTheme.StyleButton(b, bg, fg, hoverBackground: LlmChatWindow.BgHoverItem,
                pressedBackground: LlmChatWindow.BgActiveItem, borderBrush: LlmChatWindow.ColDivider);
        else
            UiTheme.StyleButton(b, bg, fg);
        return b;
    }

    private static SelectableTextBlock MakeText() => new()
    {
        FontFamily = Mono,
        FontSize   = 12,
        LineHeight = 17,
        Foreground = LlmChatWindow.ColText,
    };

    private void RefreshLive()
    {
        _live.Text = MemoryInfoCollector.Build();
        _status.Text = $"Live numbers updated {DateTime.Now:HH:mm:ss}. Dump files are temporary and deleted after the analysis.";
    }

    private async Task AnalyzeAsync()
    {
        long priv;
        using (var p = Process.GetCurrentProcess()) priv = p.PrivateMemorySize64;

        bool ok = await SimpleDialog.ConfirmAsync(this,
            "Analyze heap (temporary dump)",
            $"This writes a FULL memory dump of GTA to:\n{MemoryDumpAnalyzer.DumpFolder}\n\n" +
            $"Expected size: about {MemoryInfoCollector.FormatBytes(priv).Split('(')[0].Trim()}. " +
            "The whole app freezes for a few seconds while it is written.\n\n" +
            "The file is read, then DELETED right away. It contains everything in memory (passwords, tokens, chats) — " +
            "do not copy it while it exists.",
            okLabel: "Start");
        if (!ok) return;

        _analyzeBtn.IsEnabled = false;
        _heap.Foreground      = LlmChatWindow.ColText;
        _heap.Text            = "Working...";
        _summary.Children.Clear();
        try
        {
            var progress = new Progress<string>(s => _status.Text = s);
            HeapReport report = await MemoryDumpAnalyzer.RunAsync(_gcFirstChk.IsChecked == true, progress);
            RenderSummary(report);
            _heap.Text   = report.Details;
            _status.Text = $"Heap analysis finished {DateTime.Now:HH:mm:ss}. Temporary dump deleted.";
        }
        catch (Exception ex)
        {
            _summary.Children.Clear();
            _heap.Foreground = ColWarn;
            _heap.Text       = $"Heap analysis FAILED: {ex.GetType().Name}: {ex.Message}\n\n" +
                               "Any temporary dump file was deleted.\n" +
                               "Common causes: not enough free disk space in the temp folder, " +
                               "DOTNET_EnableDiagnostics=0 in the environment, or no DAC (mscordaccore.dll) found.";
            _status.Text     = "Heap analysis failed.";
        }
        finally
        {
            _analyzeBtn.IsEnabled = true;
        }
    }

    // ── "Where is the memory?" bar table (like Task Manager, but per memory group) ──

    private void RenderSummary(HeapReport report)
    {
        _summary.Children.Clear();

        _summary.Children.Add(new TextBlock
        {
            Text       = $"WHERE IS THE MEMORY?   process private bytes = {MemoryInfoCollector.FormatShort(report.PrivateBytes)}",
            FontSize   = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = LlmChatWindow.ColText,
            Margin     = new Thickness(0, 0, 0, 6),
        });

        foreach (CategoryRow r in report.Rows)
            _summary.Children.Add(BuildRow(r, report.PrivateBytes));

        _summary.Children.Add(new TextBlock
        {
            Text         = "Hover a row for what it means. Sizes are shallow (the object itself). Details per group are in the report below.",
            FontSize     = 10,
            Foreground   = LlmChatWindow.ColMuted,
            Margin       = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        });
    }

    private static Control BuildRow(CategoryRow r, long total)
    {
        double frac = total > 0 ? Math.Clamp((double)r.Bytes / total, 0, 1) : 0;
        var color   = Color.Parse(r.ColorHex);

        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2), Background = Brushes.Transparent };
        grid.ColumnDefinitions.Add(new ColumnDefinition(230, GridUnitType.Pixel));
        grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        grid.ColumnDefinitions.Add(new ColumnDefinition(170, GridUnitType.Pixel));
        grid.ColumnDefinitions.Add(new ColumnDefinition(120, GridUnitType.Pixel));

        var name = new TextBlock
        {
            Text = $"{r.Icon}  {r.Name}", FontSize = 12, Foreground = LlmChatWindow.ColText,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var bar = new Grid
        {
            Height = 14, Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
            Background = LlmChatWindow.BgAiBubble,
        };
        bar.ColumnDefinitions.Add(new ColumnDefinition(Math.Max(frac, 0.0001), GridUnitType.Star));
        bar.ColumnDefinitions.Add(new ColumnDefinition(Math.Max(1 - frac, 0.0001), GridUnitType.Star));
        var fill = new Border { Background = new SolidColorBrush(color), CornerRadius = new CornerRadius(2) };
        bar.Children.Add(fill);

        var size = new TextBlock
        {
            Text = $"{MemoryInfoCollector.FormatShort(r.Bytes)}   {frac * 100:N1}%",
            FontSize = 12, FontFamily = Mono, Foreground = LlmChatWindow.ColText,
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right,
        };
        var count = new TextBlock
        {
            Text = r.Count > 0 ? $"{r.Count:N0} obj" : "",
            FontSize = 11, FontFamily = Mono, Foreground = LlmChatWindow.ColMuted,
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right,
        };

        Grid.SetColumn(name, 0); Grid.SetColumn(bar, 1); Grid.SetColumn(size, 2); Grid.SetColumn(count, 3);
        grid.Children.Add(name); grid.Children.Add(bar); grid.Children.Add(size); grid.Children.Add(count);
        ToolTip.SetTip(grid, r.Hint);
        return grid;
    }
}
#endif
