#if !CO_WORKER_USE
using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime;
using System.Text;
using System.Threading;

namespace GTA.App;

/// <summary>
/// Developer-only. Builds a plain-text snapshot of the live memory state of THIS process
/// (OS counters, GC heap / generations, JIT, thread pool, loaded modules). Cheap, no dump needed.
/// Not part of the CO_WORKER_USE build.
/// </summary>
internal static class MemoryInfoCollector
{
    private static readonly string[] GenNames = { "Gen0", "Gen1", "Gen2", "LOH", "POH" };

    public static string FormatBytes(long bytes)
    {
        double b = bytes;
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int u = 0;
        while (Math.Abs(b) >= 1024 && u < units.Length - 1) { b /= 1024; u++; }
        return u == 0 ? $"{bytes:N0} B" : $"{b:N1} {units[u]}  ({bytes:N0} B)";
    }

    /// <summary>Short form without the exact byte count, e.g. "12.3 MB".</summary>
    public static string FormatShort(long bytes) => FormatBytes(bytes).Split('(')[0].Trim();

    public static string Build()
    {
        var sb = new StringBuilder();
        try { AppendProcess(sb); }  catch (Exception ex) { sb.AppendLine($"[process info failed: {ex.Message}]"); }
        try { AppendGc(sb); }       catch (Exception ex) { sb.AppendLine($"[GC info failed: {ex.Message}]"); }
        try { AppendRuntime(sb); }  catch (Exception ex) { sb.AppendLine($"[runtime info failed: {ex.Message}]"); }
        try { AppendModules(sb); }  catch (Exception ex) { sb.AppendLine($"[module info failed: {ex.Message}]"); }
        return sb.ToString();
    }

    private static void Header(StringBuilder sb, string title)
    {
        if (sb.Length > 0) sb.AppendLine();
        sb.AppendLine($"═══ {title} ".PadRight(78, '═'));
    }

    private static void Row(StringBuilder sb, string name, string value) =>
        sb.AppendLine($"  {name,-34} {value}");

    // ── OS-level process memory ──────────────────────────────────────────────
    private static void AppendProcess(StringBuilder sb)
    {
        using var p = Process.GetCurrentProcess();
        Header(sb, "PROCESS (OS view)");
        Row(sb, "PID  (match in Task Manager > Details)", p.Id.ToString());
        Row(sb, "Executable",             Environment.ProcessPath ?? "?");
        Row(sb, "Task Manager shows it as", "\".NET Host\" (default name of .NET app-host exes without version info)");
        Row(sb, "Started",                p.StartTime.ToString("yyyy-MM-dd HH:mm:ss"));
        Row(sb, "Uptime",                 (DateTime.Now - p.StartTime).ToString(@"d\.hh\:mm\:ss"));
        Row(sb, "Working set",            FormatBytes(p.WorkingSet64));
        Row(sb, "Peak working set",       FormatBytes(p.PeakWorkingSet64));
        Row(sb, "Private bytes",          FormatBytes(p.PrivateMemorySize64));
        long gcCommitted = GC.GetGCMemoryInfo(GCKind.Any).TotalCommittedBytes;
        Row(sb, "  of which .NET managed heap", FormatBytes(gcCommitted) + "  (committed, as of last GC)");
        Row(sb, "  of which native (estimate)", FormatBytes(Math.Max(0, p.PrivateMemorySize64 - gcCommitted)) + "  (bitmaps, Skia, GPU, DLLs, stacks)");
        Row(sb, "Paged memory",           FormatBytes(p.PagedMemorySize64));
        Row(sb, "Virtual memory",         FormatBytes(p.VirtualMemorySize64));
        Row(sb, "Peak virtual memory",    FormatBytes(p.PeakVirtualMemorySize64));
        Row(sb, "Threads",                p.Threads.Count.ToString());
        Row(sb, "Handles",                p.HandleCount.ToString());
        Row(sb, "64-bit process",         Environment.Is64BitProcess.ToString());
    }

    // ── GC heap ──────────────────────────────────────────────────────────────
    private static void AppendGc(StringBuilder sb)
    {
        Header(sb, "GC HEAP (managed memory)");
        Row(sb, "GC mode",                (GCSettings.IsServerGC ? "Server" : "Workstation") + ", latency " + GCSettings.LatencyMode);
        Row(sb, "LOH compaction mode",    GCSettings.LargeObjectHeapCompactionMode.ToString());
        Row(sb, "Managed heap (now)",     FormatBytes(GC.GetTotalMemory(false)));
        Row(sb, "Total allocated ever",   FormatBytes(GC.GetTotalAllocatedBytes(false)));
        Row(sb, "Total GC pause time",    GC.GetTotalPauseDuration().TotalMilliseconds.ToString("N1") + " ms");
        for (int g = 0; g <= GC.MaxGeneration; g++)
            Row(sb, $"GC count gen{g}", GC.CollectionCount(g).ToString("N0"));

        var info = GC.GetGCMemoryInfo(GCKind.Any);
        sb.AppendLine();
        sb.AppendLine($"  Last GC (#{info.Index}, gen{info.Generation}, compacted={info.Compacted}, concurrent={info.Concurrent}):");
        Row(sb, "Heap size",              FormatBytes(info.HeapSizeBytes));
        Row(sb, "Committed",              FormatBytes(info.TotalCommittedBytes));
        Row(sb, "Fragmented",             FormatBytes(info.FragmentedBytes));
        Row(sb, "Promoted",               FormatBytes(info.PromotedBytes));
        Row(sb, "Pinned objects",         info.PinnedObjectsCount.ToString("N0"));
        Row(sb, "Finalization pending",   info.FinalizationPendingCount.ToString("N0"));
        Row(sb, "Pause time %",           info.PauseTimePercentage.ToString("N2") + " %");
        Row(sb, "Machine memory load",    FormatBytes(info.MemoryLoadBytes));
        Row(sb, "Machine memory available", FormatBytes(info.TotalAvailableMemoryBytes));
        Row(sb, "High-memory-load threshold", FormatBytes(info.HighMemoryLoadThresholdBytes));

        sb.AppendLine();
        sb.AppendLine($"  {"Generation",-12}{"Size after GC",20}{"Fragmentation after",24}");
        var gens = info.GenerationInfo;
        for (int i = 0; i < gens.Length; i++)
        {
            string name = i < GenNames.Length ? GenNames[i] : $"#{i}";
            sb.AppendLine($"  {name,-12}{FormatBytes(gens[i].SizeAfterBytes),20}{FormatBytes(gens[i].FragmentationAfterBytes),24}");
        }
    }

    // ── Runtime ──────────────────────────────────────────────────────────────
    private static void AppendRuntime(StringBuilder sb)
    {
        Header(sb, "RUNTIME");
        Row(sb, ".NET version",           Environment.Version.ToString());
        Row(sb, "Loaded assemblies",      AppDomain.CurrentDomain.GetAssemblies().Length.ToString());
        Row(sb, "JIT compiled methods",   JitInfo.GetCompiledMethodCount(false).ToString("N0"));
        Row(sb, "JIT compiled IL bytes",  FormatBytes(JitInfo.GetCompiledILBytes(false)));
        Row(sb, "JIT time",               JitInfo.GetCompilationTime(false).TotalMilliseconds.ToString("N0") + " ms");
        Row(sb, "Thread pool threads",    ThreadPool.ThreadCount.ToString());
        Row(sb, "Thread pool pending",    ThreadPool.PendingWorkItemCount.ToString("N0"));
        Row(sb, "Thread pool completed",  ThreadPool.CompletedWorkItemCount.ToString("N0"));
        Row(sb, "Monitor lock contentions", Monitor.LockContentionCount.ToString("N0"));
    }

    // ── Native modules (DLLs) ────────────────────────────────────────────────
    private static void AppendModules(StringBuilder sb)
    {
        using var p = Process.GetCurrentProcess();
        var mods = p.Modules.Cast<ProcessModule>().ToList();
        Header(sb, $"NATIVE MODULES ({mods.Count} loaded, top 15 by size)");
        long total = 0;
        foreach (var m in mods) total += m.ModuleMemorySize;
        Row(sb, "Total module image size", FormatBytes(total));
        foreach (var m in mods.OrderByDescending(m => m.ModuleMemorySize).Take(15))
            sb.AppendLine($"  {FormatBytes(m.ModuleMemorySize).Split('(')[0].Trim(),12}  {m.ModuleName}");
    }
}
#endif
