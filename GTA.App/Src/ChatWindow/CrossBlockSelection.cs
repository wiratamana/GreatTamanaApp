using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace GTA.App;

internal sealed class CrossBlockSelection
{
    private enum Kind { None = 0, Full = 1, Head = 2, Tail = 3 }

    private readonly ScrollViewer _root;
    private readonly DispatcherTimer _autoScroll;

    private SelectableTextBlock? _startBlock;
    private bool  _pressed;     // left button went down on a text block
    private bool  _active;      // pointer left the start block → cross-block mode
    private Point _lastPoint;   // pointer position in _root coordinates

    private List<SelectableTextBlock> _blocks = new();
    private readonly List<SelectableTextBlock> _touched = new();
    private readonly Dictionary<SelectableTextBlock, (Kind kind, int idx)> _applied = new();

    public static void Attach(ScrollViewer root) => _ = new CrossBlockSelection(root);

    private CrossBlockSelection(ScrollViewer root)
    {
        _root = root;

        _root.AddHandler(InputElement.PointerPressedEvent,  OnPressed,  RoutingStrategies.Tunnel, handledEventsToo: true);
        _root.AddHandler(InputElement.PointerMovedEvent,    OnMoved,    RoutingStrategies.Tunnel, handledEventsToo: true);
        _root.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        _root.AddHandler(InputElement.KeyDownEvent,         OnKeyDown,  RoutingStrategies.Tunnel);

        _autoScroll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _autoScroll.Tick += (_, _) => OnAutoScrollTick();
    }

    // ── Pointer handling ──────────────────────────────────────────────────────

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        var p = e.GetCurrentPoint(_root);
        if (!p.Properties.IsLeftButtonPressed) return;

        ClearTouched();

        _startBlock = FindBlock(e.Source);
        _pressed    = _startBlock != null;
        _active     = false;
        _lastPoint  = p.Position;
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (!_pressed) return;

        var p = e.GetCurrentPoint(_root);
        if (!p.Properties.IsLeftButtonPressed)
        {
            End();
            return;
        }
        _lastPoint = p.Position;

        if (!_active)
        {
            // Stay out of the way until the pointer leaves the start block.
            var r = _startBlock == null ? null : BoundsInRoot(_startBlock);
            if (r == null) return;
            if (_lastPoint.Y >= r.Value.Top && _lastPoint.Y <= r.Value.Bottom) return;

            _active = true;
            _blocks = CollectBlocks();
            Touch(_startBlock!);
            _autoScroll.Start();
        }

        Apply();
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e) => End();

    private void End()
    {
        _pressed = false;
        _active  = false;
        _autoScroll.Stop();
    }

    private void OnAutoScrollTick()
    {
        if (!_active || !_pressed)
        {
            _autoScroll.Stop();
            return;
        }

        const double edge = 28;
        double h  = _root.Bounds.Height;
        double dy = 0;
        if (_lastPoint.Y < edge)          dy = -((edge - _lastPoint.Y) * 0.5 + 2);
        else if (_lastPoint.Y > h - edge) dy =  ((_lastPoint.Y - (h - edge)) * 0.5 + 2);
        if (dy == 0) return;

        dy = Math.Max(-40, Math.Min(40, dy));
        double max = Math.Max(0, _root.Extent.Height - _root.Viewport.Height);
        double y   = Math.Max(0, Math.Min(max, _root.Offset.Y + dy));
        if (Math.Abs(y - _root.Offset.Y) < 0.5) return;

        _root.Offset = new Vector(_root.Offset.X, y);
        Apply();
    }

    // ── Selection logic ───────────────────────────────────────────────────────

    private void Apply()
    {
        if (_startBlock == null) return;

        var items = new List<(SelectableTextBlock B, Rect R)>(_blocks.Count);
        foreach (var b in _blocks)
        {
            var r = BoundsInRoot(b);
            if (r != null) items.Add((b, r.Value));
        }

        int s = items.FindIndex(x => ReferenceEquals(x.B, _startBlock));
        if (s < 0) return;

        Point pt = _lastPoint;

        // Which block is the pointer on?
        int t = -1;
        for (int i = 0; i < items.Count; i++)
            if (items[i].R.Contains(pt)) { t = i; break; }
        if (t < 0)
            for (int i = 0; i < items.Count; i++)
                if (pt.Y >= items[i].R.Top && pt.Y <= items[i].R.Bottom) { t = i; break; }
        if (t < 0)
        {
            // In a gap (or outside everything): the last block that is fully above the pointer.
            for (int i = items.Count - 1; i >= 0; i--)
                if (items[i].R.Bottom <= pt.Y) { t = i; break; }
            if (t < 0) t = 0;
        }

        bool downward = t > s;

        for (int i = 0; i < items.Count; i++)
        {
            if (i == s) continue;   // the start block selects itself

            var b = items[i].B;
            if (t != s && ((downward && i > s && i < t) || (!downward && i < s && i > t)))
            {
                SetState(b, Kind.Full, 0);
            }
            else if (i == t && t != s)
            {
                var (kind, idx) = TargetState(items[i].B, items[i].R, downward, pt);
                SetState(b, kind, idx);
            }
            else
            {
                SetState(b, Kind.None, 0);
            }
        }
    }

    /// <summary>Partial selection for the block that is under the pointer.</summary>
    private static (Kind, int) TargetState(SelectableTextBlock b, Rect rect, bool downward, Point pt)
    {
        var rel    = new Point(pt.X - rect.X, pt.Y - rect.Y);
        bool above = rel.Y < 0;
        bool below = rel.Y > rect.Height;

        if (downward)
        {
            // Selection runs from the start of this block up to the pointer.
            if (above) return (Kind.None, 0);
            if (below) return (Kind.Full, 0);
            int idx = HitIndex(b, rel);
            return idx <= 0 ? (Kind.None, 0) : (Kind.Head, idx);
        }
        else
        {
            // Selection runs from the pointer to the end of this block.
            if (below) return (Kind.None, 0);
            if (above) return (Kind.Full, 0);
            return (Kind.Tail, HitIndex(b, rel));
        }
    }

    private static int HitIndex(SelectableTextBlock b, Point rel)
    {
        try
        {
            var local = new Point(rel.X - b.Padding.Left, rel.Y - b.Padding.Top);
            var hit   = b.TextLayout.HitTestPoint(local);
            int idx   = hit.CharacterHit.FirstCharacterIndex + hit.CharacterHit.TrailingLength;
            return Math.Max(0, idx);
        }
        catch
        {
            return 0;
        }
    }

    private void SetState(SelectableTextBlock b, Kind kind, int idx)
    {
        var key = (kind, idx);
        if (_applied.TryGetValue(b, out var cur))
        {
            if (cur == key) return;
        }
        else if (kind == Kind.None)
        {
            return;   // never touched, nothing to clear
        }

        _applied[b] = key;

        try
        {
            switch (kind)
            {
                case Kind.None:
                    b.ClearSelection();
                    break;
                case Kind.Full:
                    b.SelectAll();
                    break;
                case Kind.Head:
                    b.SelectionStart = 0;
                    b.SelectionEnd   = idx;
                    break;
                case Kind.Tail:
                    b.SelectAll();
                    b.SelectionStart = idx;
                    break;
            }
        }
        catch
        {
            // Text may have been rebuilt while streaming — ignore, next move fixes it.
        }

        if (kind != Kind.None) Touch(b);
    }

    private void Touch(SelectableTextBlock b)
    {
        if (!_touched.Contains(b)) _touched.Add(b);
    }

    private void ClearTouched()
    {
        foreach (var b in _touched)
        {
            try { b.ClearSelection(); } catch { }
        }
        _touched.Clear();
        _applied.Clear();
    }

    // ── Keyboard: Ctrl+C (multi-block) and Ctrl+A (everything) ────────────────

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;

        if (e.Key == Key.C)
        {
            var selected = _touched
                .Where(b => b.GetVisualRoot() != null && !string.IsNullOrEmpty(b.SelectedText))
                .ToList();
            if (selected.Count < 2) return;   // single block → normal Avalonia copy

            var ordered = selected
                .Select(b => (B: b, R: BoundsInRoot(b)))
                .Where(x => x.R != null)
                .OrderBy(x => Math.Round(x.R!.Value.Top))
                .ThenBy(x => x.R!.Value.Left)
                .Select(x => x.B.SelectedText);

            string text = string.Join("\n", ordered);
            var clipboard = TopLevel.GetTopLevel(_root)?.Clipboard;
            if (clipboard != null)
            {
                _ = clipboard.SetTextAsync(text);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.A)
        {
            // Only when a chat text block has focus (never steal Ctrl+A from the input box).
            if (FindBlock(e.Source) == null) return;

            ClearTouched();
            foreach (var b in CollectBlocks())
            {
                try { b.SelectAll(); } catch { }
                _applied[b] = (Kind.Full, 0);
                Touch(b);
            }
            e.Handled = true;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static SelectableTextBlock? FindBlock(object? source)
        => (source as Visual)?.GetSelfAndVisualAncestors().OfType<SelectableTextBlock>().FirstOrDefault();

    private Rect? BoundsInRoot(Visual v)
    {
        if (v.GetVisualRoot() == null) return null;
        var tl = v.TranslatePoint(new Point(0, 0), _root);
        if (tl == null) return null;
        return new Rect(tl.Value, v.Bounds.Size);
    }

    /// <summary>All visible text blocks under the root, in on-screen reading order.</summary>
    private List<SelectableTextBlock> CollectBlocks()
    {
        var list = new List<(SelectableTextBlock B, Rect R)>();
        foreach (var b in _root.GetVisualDescendants().OfType<SelectableTextBlock>())
        {
            if (!b.IsEffectivelyVisible || b.Bounds.Height <= 0) continue;
            var r = BoundsInRoot(b);
            if (r != null) list.Add((b, r.Value));
        }

        return list
            .OrderBy(x => Math.Round(x.R.Top))
            .ThenBy(x => x.R.Left)
            .Select(x => x.B)
            .ToList();
    }
}
