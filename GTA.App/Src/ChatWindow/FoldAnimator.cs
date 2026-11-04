using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;

namespace GTA.App;

/// <summary>
/// Smooth open / close animation for the body of a foldable chat widget
/// (tool call, file card, thinking). Animates MaxHeight + Opacity (ease-out).
/// The animated control should have <c>ClipToBounds = true</c> so nothing spills out while it shrinks.
/// Both methods return the running timer (or null when there was nothing to animate) so the caller can stop it.
/// </summary>
internal static class FoldAnimator
{
    private const double DefaultMs = 240;

    /// <summary>Shrink <paramref name="target"/> to nothing, then hide it.</summary>
    public static DispatcherTimer? Close(Control target, Action? done = null, double ms = DefaultMs)
    {
        double start = target.Bounds.Height;
        if (!target.IsVisible || start < 4)
        {
            target.IsVisible = false;
            Reset(target);
            done?.Invoke();
            return null;
        }

        double startOpacity = target.Opacity;   // a cancelled open animation continues from where it was
        target.MaxHeight = start;

        return Run(ms,
            e =>
            {
                target.MaxHeight = Math.Max(0, start * (1.0 - e));
                target.Opacity   = startOpacity * (1.0 - e);
            },
            () =>
            {
                target.IsVisible = false;
                Reset(target);
                done?.Invoke();
            });
    }

    /// <summary>Show <paramref name="target"/> and grow it to its natural height.</summary>
    public static DispatcherTimer? Open(Control target, Action? done = null, double ms = DefaultMs)
    {
        Reset(target);
        target.IsVisible = true;

        double full = MeasureHeight(target);
        if (full < 4)
        {
            done?.Invoke();
            return null;
        }

        target.MaxHeight = 0;
        target.Opacity   = 0;

        return Run(ms,
            e =>
            {
                target.MaxHeight = full * e;
                target.Opacity   = e;
            },
            () =>
            {
                Reset(target);
                done?.Invoke();
            });
    }

    private static void Reset(Control target)
    {
        target.MaxHeight = double.PositiveInfinity;
        target.Opacity   = 1;
    }

    private static double MeasureHeight(Control c)
    {
        double w = c.Bounds.Width;
        if (w < 1 && c.Parent is Layoutable p) w = p.Bounds.Width;
        if (w < 1) w = 600;
        try
        {
            c.Measure(new Size(w, double.PositiveInfinity));
            return c.DesiredSize.Height;
        }
        catch
        {
            return 0;
        }
    }

    private static DispatcherTimer Run(double ms, Action<double> step, Action finish)
    {
        var sw    = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            double p = Math.Min(1.0, sw.Elapsed.TotalMilliseconds / ms);
            double e = 1.0 - Math.Pow(1.0 - p, 3);   // ease-out
            step(e);
            if (p >= 1.0)
            {
                timer.Stop();
                finish();
            }
        };
        timer.Start();
        return timer;
    }
}
