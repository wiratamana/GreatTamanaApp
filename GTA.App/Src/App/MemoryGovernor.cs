using System;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using GTA.Core;

namespace GTA.App;

/// <summary>
/// Keeps the memory of the process small for a whole day.
///
/// .NET keeps freed memory "for later" and does not compact the Large Object Heap by itself. A chat app that
/// saves / sends big JSON every round (strings of many MB) therefore ends with a huge committed heap full of holes
/// even when little is really in use. This class:
///   1. runs a COMPACTING, memory-releasing GC when the app is idle, after a chat switch / new chat and after a turn;
///   2. every minute drops chats that are finished and not on screen (see LlmChatWindow.SweepIdleSessions);
///   3. trims the working set while the window is in the background.
/// Everything here is best-effort and never throws.
/// </summary>
internal static class MemoryGovernor
{
    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    /// <summary>Do not run two compactions closer than this (a compaction is cheap, but never needed in a burst).</summary>
    private const long MinGapMs = 4_000;

    /// <summary>Periodic check. Cheap when nothing is to do.</summary>
    private const int TickMs = 60_000;

    private static int  _busy;
    private static long _lastRunTick = -MinGapMs;
    private static Timer? _timer;

    /// <summary>Start the 1-minute housekeeping. Call once, on the UI thread, after the main window exists.</summary>
    public static void Start()
    {
        if (_timer != null) return;

        _timer = new Timer(_ =>
        {
            // Chat sweeping touches UI objects → UI thread.
            Dispatcher.UIThread.Post(() =>
            {
                try { LlmChatWindow.Instance?.SweepIdleSessions(); }
                catch (Exception ex) { SessionFileLogger.LogException("MemoryGovernor sweep failed", ex); }

                bool windowInBackground = LlmChatWindow.Instance is { } w && !w.IsActive;
                Request("periodic", trimWorkingSet: windowInBackground);
            }, DispatcherPriority.Background);
        }, null, TickMs, TickMs);
    }

    /// <summary>
    /// Ask for a compacting GC on a background thread. Returns at once. Safe to call from any thread, as often as
    /// you like (calls that come too early after the last run are ignored).
    /// </summary>
    public static void Request(string reason, bool trimWorkingSet = false)
    {
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastRunTick) < MinGapMs) return;
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;

        _ = Task.Run(() =>
        {
            try { Run(reason, trimWorkingSet); }
            catch (Exception ex) { SessionFileLogger.LogException("MemoryGovernor run failed", ex); }
            finally
            {
                Interlocked.Exchange(ref _lastRunTick, Environment.TickCount64);
                Volatile.Write(ref _busy, 0);
            }
        });
    }

    private static void Run(string reason, bool trimWorkingSet)
    {
        long heapBefore = GC.GetTotalMemory(false);
        long wsBefore   = Environment.WorkingSet;

        // Compact the Large Object Heap once as well (it never is, by default) and give the memory back to Windows.
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);

        if (trimWorkingSet)
        {
            try { EmptyWorkingSet(Process.GetCurrentProcess().Handle); }
            catch { /* best effort */ }
        }

        long heapAfter = GC.GetTotalMemory(false);
        long wsAfter   = Environment.WorkingSet;

        string line =
            $"[{reason}] GC heap {heapBefore / 1048576}MB -> {heapAfter / 1048576}MB, " +
            $"working set {wsBefore / 1048576}MB -> {wsAfter / 1048576}MB" +
            (trimWorkingSet ? " (WS trimmed)" : "");

        // Always log the big ones (that is what you want to see when something grows); the rest only in verbose mode.
        if (heapBefore > 150L * 1048576 || wsBefore > 300L * 1048576)
            SessionFileLogger.Log("MEMORY", line);
        else
            SessionFileLogger.LogVerbose("MEMORY", line);
    }
}
