#if !CO_WORKER_USE
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Runtime;

namespace GTA.App;

/// <summary>The groups every heap object is sorted into, so the numbers are easy to read.</summary>
internal enum MemCat { Strings, Avalonia, Images, RawBytes, GtaApp, Other }

/// <summary>One bar row of the "Where is the memory?" table.</summary>
internal sealed record CategoryRow(string Icon, string Name, long Bytes, long Count, string Hint, string ColorHex);

/// <summary>Result of a heap analysis: bar rows for the UI + the full text report.</summary>
internal sealed class HeapReport
{
    public long PrivateBytes;
    public readonly List<CategoryRow> Rows = new();
    public string Details = "";
}

/// <summary>
/// Developer-only. Heap analysis by group (strings / Avalonia / images / raw bytes / GTA / other),
/// using a TEMPORARY full memory dump of this very process:
///   1. write a full dump into %TEMP%\GTA_MemDump   (DiagnosticsClient.WriteDump)
///   2. read it with ClrMD, sort every object into a group
///   3. DELETE the dump file again - always, also when step 1 or 2 fails.
/// The dump is never kept (disk space is tight). Leftover dumps (e.g. after a crash) are removed by CleanupStale().
/// Not part of the CO_WORKER_USE build.
/// </summary>
internal static class MemoryDumpAnalyzer
{
    private const string FilePrefix = "GTA_memdump_";

    private static int _running;

    public static string DumpFolder => Path.Combine(Path.GetTempPath(), "GTA_MemDump");

    /// <summary>Deletes dump files left behind by a crash / kill. Does nothing while an analysis is running.</summary>
    public static void CleanupStale()
    {
        if (Volatile.Read(ref _running) == 1) return;
        try
        {
            if (!Directory.Exists(DumpFolder)) return;
            foreach (string f in Directory.EnumerateFiles(DumpFolder, FilePrefix + "*.dmp"))
                DeleteWithRetry(f);
        }
        catch { /* best effort */ }
    }

    public static Task<HeapReport> RunAsync(bool gcFirst, IProgress<string> progress) =>
        Task.Run(() => Run(gcFirst, progress));

    private static HeapReport Run(bool gcFirst, IProgress<string> progress)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
            throw new InvalidOperationException("A heap analysis is already running.");

        string?    path = null;
        HeapReport report;
        bool       deleted = true;
        try
        {
            Directory.CreateDirectory(DumpFolder);

            // A full dump is about as big as the private memory of the process. Refuse early instead of filling the disk.
            long priv;
            using (var p = Process.GetCurrentProcess()) priv = p.PrivateMemorySize64;
            long need = (long)(priv * 1.2);
            long free = new DriveInfo(Path.GetPathRoot(DumpFolder)!).AvailableFreeSpace;
            if (free < need)
                throw new IOException(
                    $"Not enough free disk space for the temporary dump: need about {MemoryInfoCollector.FormatShort(need)}, " +
                    $"free {MemoryInfoCollector.FormatShort(free)} on {Path.GetPathRoot(DumpFolder)}. Nothing was written.");

            if (gcFirst)
            {
                progress.Report("Running a full GC first...");
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                using var p2 = Process.GetCurrentProcess();
                priv = p2.PrivateMemorySize64;
            }

            path = Path.Combine(DumpFolder, $"{FilePrefix}{DateTime.Now:yyyyMMdd_HHmmss}.dmp");

            progress.Report("Writing temporary dump (the app freezes for a few seconds)...");
            var sw = Stopwatch.StartNew();
            new DiagnosticsClient(Environment.ProcessId).WriteDump(DumpType.Full, path);
            long dumpMs    = sw.ElapsedMilliseconds;
            long dumpBytes = new FileInfo(path).Length;

            progress.Report($"Dump written ({MemoryInfoCollector.FormatShort(dumpBytes)}). Analyzing heap...");
            sw.Restart();
            report = Analyze(path, dumpBytes, dumpMs, priv, sw);
        }
        finally
        {
            if (path != null)
            {
                progress.Report("Deleting temporary dump...");
                deleted = DeleteWithRetry(path);
            }
            Volatile.Write(ref _running, 0);
        }

        report.Details += Environment.NewLine +
            (deleted ? "Temporary dump file: DELETED." : $"WARNING: could not delete the temporary dump: {path}");
        return report;
    }

    private static bool DeleteWithRetry(string path)
    {
        for (int i = 0; i < 15; i++)
        {
            try
            {
                if (!File.Exists(path)) return true;
                File.Delete(path);
                return !File.Exists(path);
            }
            catch { Thread.Sleep(300); }
        }
        return !File.Exists(path);
    }

    /// <summary>The DAC (mscordaccore.dll) of the runtime we are running on is the one that matches the dump.</summary>
    private static string? FindLocalDac()
    {
        var dirs = new List<string> { RuntimeEnvironment.GetRuntimeDirectory() };
        string? exeDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (exeDir != null) dirs.Add(exeDir);
        dirs.Add(AppContext.BaseDirectory);

        foreach (string d in dirs)
        {
            string f = Path.Combine(d, "mscordaccore.dll");
            if (File.Exists(f)) return f;
        }
        return null;
    }

    // ── Classification ───────────────────────────────────────────────────────

    private sealed class TypeKind
    {
        public string Name = "";
        public string Group = "";
        public MemCat Cat;
        public bool   IsString, IsByteArray, IsControl;
    }

    private sealed class Acc
    {
        public string Name = "";
        public string Group = "";
        public MemCat Cat;
        public long   Count;
        public long   Bytes;
    }

    private sealed class Buckets
    {
        // < 1 KB, < 10 KB, < 100 KB, < 1 MB, >= 1 MB
        private static readonly long[] Edges = { 1_024, 10_240, 102_400, 1_048_576 };
        public readonly long[] Count = new long[5];
        public readonly long[] Bytes = new long[5];

        public void Add(ulong size)
        {
            int i = 0;
            while (i < Edges.Length && (long)size >= Edges[i]) i++;
            Count[i]++;
            Bytes[i] += (long)size;
        }

        public void Append(StringBuilder sb, string what)
        {
            string[] names = { "< 1 KB", "1 - 10 KB", "10 - 100 KB", "100 KB - 1 MB", ">= 1 MB" };
            sb.AppendLine($"  {what} by size:");
            for (int i = 0; i < 5; i++)
                sb.AppendLine($"    {names[i],-14} {Count[i],10:N0} objects  {MemoryInfoCollector.FormatShort(Bytes[i]),12}");
        }
    }

    private sealed class TopN
    {
        private readonly PriorityQueue<(ulong Addr, string Label), ulong> _q = new();
        private readonly int _n;
        public TopN(int n) { _n = n; }

        public void Add(ulong addr, string label, ulong size)
        {
            if (_q.Count < _n) _q.Enqueue((addr, label), size);
            else if (_q.TryPeek(out _, out ulong min) && size > min)
            {
                _q.Dequeue();
                _q.Enqueue((addr, label), size);
            }
        }

        public List<(ulong Size, ulong Addr, string Label)> Sorted()
        {
            var list = new List<(ulong, ulong, string)>();
            var copy = new PriorityQueue<(ulong Addr, string Label), ulong>(_q.UnorderedItems);
            while (copy.TryDequeue(out var item, out ulong prio)) list.Add((prio, item.Addr, item.Label));
            return list.OrderByDescending(x => x.Item1).ToList();
        }
    }

    private static string NamespaceOf(string typeName)
    {
        int lt = typeName.IndexOf('<');
        string n = lt >= 0 ? typeName[..lt] : typeName;
        int dot = n.LastIndexOf('.');
        return dot > 0 ? n[..dot] : "(global)";
    }

    private static bool InheritsFrom(ClrType? t, string baseName)
    {
        for (int i = 0; t != null && i < 40; i++, t = t.BaseType)
            if (t.Name == baseName) return true;
        return false;
    }

    private static TypeKind Classify(ClrType? t)
    {
        string name = t?.Name ?? "<unknown type>";
        var k = new TypeKind { Name = name, Group = NamespaceOf(name) };

        if (name == "System.String")                     { k.IsString = true; k.Cat = MemCat.Strings; }
        else if (name == "System.Byte[]")                { k.IsByteArray = true; k.Cat = MemCat.RawBytes; }
        else if (name == "System.Char[]" || name == "System.Text.StringBuilder") k.Cat = MemCat.Strings;
        else if (name.StartsWith("SkiaSharp.", StringComparison.Ordinal) ||
                 (name.StartsWith("Avalonia.", StringComparison.Ordinal) &&
                  (name.Contains(".Imaging.") ||
                   (name.StartsWith("Avalonia.Skia.", StringComparison.Ordinal) &&
                    (name.Contains("Bitmap") || name.Contains("Image") || name.Contains("Surface") || name.Contains("Picture"))))))
            k.Cat = MemCat.Images;
        else if (name.StartsWith("Avalonia.", StringComparison.Ordinal)) k.Cat = MemCat.Avalonia;
        else if (name.StartsWith("GTA.", StringComparison.Ordinal))      k.Cat = MemCat.GtaApp;
        else                                                              k.Cat = MemCat.Other;

        if (k.Cat is MemCat.Avalonia or MemCat.GtaApp)
            k.IsControl = InheritsFrom(t, "Avalonia.Controls.Control");
        return k;
    }

    /// <summary>Image file type from the first bytes of a byte[]; null if it is not a known image file.</summary>
    private static string? SniffImage(byte[] h)
    {
        if (h.Length < 12) return null;
        if (h[0] == 0x89 && h[1] == 0x50 && h[2] == 0x4E && h[3] == 0x47) return "PNG";
        if (h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF)                 return "JPEG";
        if (h[0] == 0x47 && h[1] == 0x49 && h[2] == 0x46 && h[3] == 0x38) return "GIF";
        if (h[0] == 0x52 && h[1] == 0x49 && h[2] == 0x46 && h[3] == 0x46 &&
            h[8] == 0x57 && h[9] == 0x45 && h[10] == 0x42 && h[11] == 0x50) return "WEBP";
        if (h[0] == 0x00 && h[1] == 0x00 && h[2] == 0x01 && h[3] == 0x00) return "ICO";
        return null;
    }

    /// <summary>Image type if this (long) string is a base64 image or a data: URL; null otherwise.</summary>
    private static string? SniffBase64Image(string? head)
    {
        if (string.IsNullOrEmpty(head)) return null;
        if (head.StartsWith("iVBORw0KGgo", StringComparison.Ordinal)) return "PNG";
        if (head.StartsWith("/9j/",        StringComparison.Ordinal)) return "JPEG";
        if (head.StartsWith("R0lGOD",      StringComparison.Ordinal)) return "GIF";
        if (head.StartsWith("UklGR",       StringComparison.Ordinal)) return "WEBP";
        if (head.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) return "data-URL";
        return null;
    }

    private static string StringKind(string s)
    {
        string t = s.TrimStart();
        if (t.StartsWith("{") || t.StartsWith("[")) return "JSON?";
        if (t.Length > 2 && t[1] == ':' && (t[2] == '\\' || t[2] == '/')) return "path";
        if (t.StartsWith("http", StringComparison.OrdinalIgnoreCase))      return "URL";
        if (t.StartsWith("<"))                                              return "XML/HTML?";
        return "text";
    }

    private static string Preview(string? s)
    {
        if (s == null) return "";
        var sb = new StringBuilder(s.Length);
        foreach (char c in s) sb.Append(char.IsControl(c) ? ' ' : c);
        return sb.ToString();
    }

    // ── Analysis ─────────────────────────────────────────────────────────────

    private static HeapReport Analyze(string dumpPath, long dumpBytes, long dumpMs, long privBytes, Stopwatch sw)
    {
        using DataTarget target = DataTarget.LoadDump(dumpPath);

        ClrInfo? clr = target.ClrVersions.FirstOrDefault();
        if (clr == null) throw new InvalidOperationException("No .NET runtime found in the dump.");

        string? dac = FindLocalDac();
        ClrRuntime runtime = dac != null ? clr.CreateRuntime(dac) : clr.CreateRuntime();
        ClrHeap    heap    = runtime.Heap;
        if (!heap.CanWalkHeap)
            throw new InvalidOperationException("The heap in the dump cannot be walked (GC was running during the dump?). Try again.");

        var kinds       = new Dictionary<ulong, TypeKind>();
        var byType      = new Dictionary<(string Name, MemCat Cat), Acc>();
        var catBytes    = new long[6];
        var catCount    = new long[6];
        var controlType = new Dictionary<string, long>();
        var imgFormats  = new Dictionary<string, long[]>();   // format -> [count, bytes]
        var strBuckets  = new Buckets();
        var byteBuckets = new Buckets();
        var topStrings  = new TopN(15);
        var topImages   = new TopN(12);
        var topRaw      = new TopN(12);

        long objCount = 0, liveBytes = 0, freeBytes = 0;
        long controlsAva = 0, controlsGta = 0;
        long b64Count = 0, b64Bytes = 0;

        foreach (ClrObject obj in heap.EnumerateObjects())
        {
            ClrType? t    = obj.Type;
            ulong    size = obj.Size;
            ulong    mt   = t?.MethodTable ?? 0;

            if (!kinds.TryGetValue(mt, out TypeKind? k))
                kinds[mt] = k = Classify(t);

            if (k.Name == "Free") { freeBytes += (long)size; continue; }

            MemCat cat   = k.Cat;
            string label = k.Name;

            if (k.IsString)
            {
                strBuckets.Add(size);
                if (size >= 1000)
                {
                    string? fmt = SniffBase64Image(obj.AsString(12));
                    if (fmt != null)
                    {
                        cat = MemCat.Images;
                        label = "System.String (base64 image)";
                        b64Count++; b64Bytes += (long)size;
                        topImages.Add(obj.Address, $"base64 {fmt} string", size);
                    }
                    else topStrings.Add(obj.Address, "", size);
                }
            }
            else if (k.IsByteArray)
            {
                byteBuckets.Add(size);
                if (size >= 1024)
                {
                    string? fmt = null;
                    try { fmt = SniffImage(obj.AsArray().ReadValues<byte>(0, 12) ?? Array.Empty<byte>()); } catch { }
                    if (fmt != null)
                    {
                        cat = MemCat.Images;
                        label = "System.Byte[] (image file)";
                        if (!imgFormats.TryGetValue(fmt, out long[]? f)) imgFormats[fmt] = f = new long[2];
                        f[0]++; f[1] += (long)size;
                        topImages.Add(obj.Address, $"{fmt} file bytes (byte[])", size);
                    }
                    else topRaw.Add(obj.Address, "byte[] (unknown content)", size);
                }
            }

            if (k.IsControl)
            {
                if (k.Cat == MemCat.Avalonia) controlsAva++; else controlsGta++;
                controlType[k.Name] = controlType.GetValueOrDefault(k.Name) + 1;
            }

            objCount++;
            liveBytes += (long)size;
            catBytes[(int)cat] += (long)size;
            catCount[(int)cat]++;

            var key = (label, cat);
            if (!byType.TryGetValue(key, out Acc? acc))
                byType[key] = acc = new Acc { Name = label, Group = k.Group, Cat = cat };
            acc.Count++;
            acc.Bytes += (long)size;
        }

        // Committed managed memory (live + free + not yet used) at dump time.
        long committed = 0;
        try { foreach (var s in heap.Segments) committed += (long)s.CommittedMemory.Length; }
        catch { committed = liveBytes + freeBytes; }
        long managedUnused = Math.Max(0, committed - liveBytes);
        long native        = Math.Max(0, privBytes - committed);

        var report = new HeapReport { PrivateBytes = privBytes };
        string F(long b) => MemoryInfoCollector.FormatShort(b);

        // ── Bar rows ─────────────────────────────────────────────────────────
        report.Rows.Add(new CategoryRow("🔤", "Strings",              catBytes[(int)MemCat.Strings],  catCount[(int)MemCat.Strings],
            "System.String, char[], StringBuilder. Chat text, JSON, base64 text, paths...", "#4F8EF7"));
        report.Rows.Add(new CategoryRow("🧩", "Avalonia objects",     catBytes[(int)MemCat.Avalonia], catCount[(int)MemCat.Avalonia],
            "Avalonia.* controls, brushes, styles, layout and binding objects (shallow size: what they hold is counted in other rows).", "#A06BE0"));
        report.Rows.Add(new CategoryRow("🖼", "Images (managed)",     catBytes[(int)MemCat.Images],   catCount[(int)MemCat.Images],
            "Image FILE bytes (PNG/JPEG/GIF/WEBP in byte[]), base64 image strings, Avalonia/Skia bitmap wrapper objects. Decoded pixels are in 'Native'.", "#E0A04F"));
        report.Rows.Add(new CategoryRow("📦", "Raw byte arrays",      catBytes[(int)MemCat.RawBytes], catCount[(int)MemCat.RawBytes],
            "byte[] whose content is not a known image file: buffers, streams, pixel data, encoded files of other types.", "#D9604F"));
        report.Rows.Add(new CategoryRow("🏠", "GTA app objects",      catBytes[(int)MemCat.GtaApp],   catCount[(int)MemCat.GtaApp],
            "GTA.* types: chat sessions, messages, tool objects, custom controls.", "#4FBF8A"));
        report.Rows.Add(new CategoryRow("⚙", "Other .NET objects",    catBytes[(int)MemCat.Other],    catCount[(int)MemCat.Other],
            "Everything else on the managed heap: collections, System.*, JSON, HTTP, threading...", "#8A94A6"));
        report.Rows.Add(new CategoryRow("♻", "Managed free / unused", managedUnused, 0,
            "Committed GC memory without live objects: holes (fragmentation) and spare space the GC keeps for later.", "#5C6573"));
        report.Rows.Add(new CategoryRow("🧱", "Native / unmanaged",   native, 0,
            "ESTIMATE = private bytes - managed committed. Decoded bitmaps (Skia), GPU/Direct3D, fonts, native DLL data, thread stacks, the runtime itself.", "#C25B8E"));

        // ── Details text ─────────────────────────────────────────────────────
        var sb = new StringBuilder();

        sb.AppendLine("═══ HEAP ANALYSIS (from temporary full dump) ".PadRight(78, '═'));
        sb.AppendLine($"  Dump size / write time        {F(dumpBytes)} / {dumpMs:N0} ms");
        sb.AppendLine($"  Analysis time                 {sw.ElapsedMilliseconds:N0} ms");
        sb.AppendLine($"  Runtime in dump               {clr.Version}  (DAC: {(dac != null ? "local" : "symbol server")})");
        sb.AppendLine($"  Objects on heap               {objCount:N0}  ({F(liveBytes)})");
        sb.AppendLine($"  Managed committed             {F(committed)}   (free objects: {F(freeBytes)})");
        sb.AppendLine($"  Process private bytes         {F(privBytes)}   -> native/unmanaged estimate {F(native)}");
        sb.AppendLine("  Sizes are SHALLOW (the object itself). A dump also contains garbage not yet collected.");

        sb.AppendLine();
        sb.AppendLine("═══ WHERE IS THE MEMORY? ".PadRight(78, '═'));
        foreach (var r in report.Rows)
        {
            double pct = privBytes > 0 ? r.Bytes * 100.0 / privBytes : 0;
            sb.AppendLine($"  {r.Icon} {r.Name,-24} {F(r.Bytes),12} {pct,5:N1}%  {(r.Count > 0 ? r.Count.ToString("N0") + " objects" : "")}");
        }

        // STRINGS
        sb.AppendLine();
        sb.AppendLine("═══ 🔤 STRINGS ".PadRight(78, '═'));
        strBuckets.Append(sb, "Strings");
        AppendTypes(sb, byType, MemCat.Strings, 6, "Types");
        sb.AppendLine();
        sb.AppendLine("  15 biggest strings (kind guessed from the first characters):");
        foreach (var (size, addr, _) in topStrings.Sorted())
        {
            string? s = null;
            try { s = heap.GetObject(addr).AsString(400); } catch { }
            string kind = s == null ? "?" : StringKind(s);
            string prev = Preview(s);
            if (prev.Length > 70) prev = prev[..70] + "…";
            sb.AppendLine($"  {F((long)size),11}  {kind,-9} 0x{addr:X}  \"{prev}\"");
        }

        // AVALONIA
        sb.AppendLine();
        sb.AppendLine("═══ 🧩 AVALONIA OBJECTS ".PadRight(78, '═'));
        sb.AppendLine($"  Live UI controls (Avalonia.Controls.Control subclasses): {controlsAva + controlsGta:N0}");
        sb.AppendLine($"      from Avalonia itself: {controlsAva:N0}     from GTA custom controls: {controlsGta:N0}");
        sb.AppendLine("      (a number that keeps growing while you use the app = leaked widgets)");
        sb.AppendLine("  Top control types by count:");
        foreach (var kv in controlType.OrderByDescending(x => x.Value).Take(12))
            sb.AppendLine($"    {kv.Value,10:N0}  {kv.Key}");
        AppendGroups(sb, byType, MemCat.Avalonia, 10);
        AppendTypes(sb, byType, MemCat.Avalonia, 12, "Top types");

        // IMAGES
        sb.AppendLine();
        sb.AppendLine("═══ 🖼 IMAGES (managed memory) ".PadRight(78, '═'));
        sb.AppendLine($"  Image files held as byte[]:    {imgFormats.Values.Sum(v => v[0]):N0} arrays, {F(imgFormats.Values.Sum(v => v[1]))}");
        foreach (var kv in imgFormats.OrderByDescending(x => x.Value[1]))
            sb.AppendLine($"      {kv.Key,-8} {kv.Value[0],8:N0} files  {F(kv.Value[1]),12}");
        sb.AppendLine($"  Images held as base64 strings: {b64Count:N0} strings, {F(b64Bytes)}   (a base64 string is 2 bytes per char!)");
        AppendTypes(sb, byType, MemCat.Images, 12, "All image-related types");
        sb.AppendLine();
        sb.AppendLine("  Biggest image objects:");
        foreach (var (size, addr, label) in topImages.Sorted())
            sb.AppendLine($"  {F((long)size),11}  0x{addr:X}  {label}");
        sb.AppendLine();
        sb.AppendLine("  NOTE: decoded bitmaps (pixels) are NOT on the .NET heap. Avalonia keeps them in native Skia memory,");
        sb.AppendLine($"  so they show up in the 'Native / unmanaged' row ({F(native)} now), not here.");

        // RAW BYTES
        sb.AppendLine();
        sb.AppendLine("═══ 📦 RAW BYTE ARRAYS ".PadRight(78, '═'));
        byteBuckets.Append(sb, "All byte[]");
        sb.AppendLine();
        sb.AppendLine("  12 biggest byte[] that are not a known image file:");
        foreach (var (size, addr, label) in topRaw.Sorted())
            sb.AppendLine($"  {F((long)size),11}  0x{addr:X}  {label}");

        // GTA
        sb.AppendLine();
        sb.AppendLine("═══ 🏠 GTA APP OBJECTS ".PadRight(78, '═'));
        AppendTypes(sb, byType, MemCat.GtaApp, 20, "Top types");

        // OTHER
        sb.AppendLine();
        sb.AppendLine("═══ ⚙ OTHER .NET OBJECTS ".PadRight(78, '═'));
        AppendGroups(sb, byType, MemCat.Other, 12);
        AppendTypes(sb, byType, MemCat.Other, 20, "Top types");

        // Segments
        try
        {
            sb.AppendLine();
            sb.AppendLine("═══ GC HEAP SEGMENTS ".PadRight(78, '═'));
            foreach (var g in heap.Segments.GroupBy(s => s.Kind).OrderBy(g => g.Key.ToString()))
                sb.AppendLine($"    {g.Key,-14} {g.Count(),5} segment(s)  {F((long)g.Sum(s => (double)s.Length)),12}");
        }
        catch (Exception ex) { sb.AppendLine($"  [segments unavailable: {ex.Message}]"); }

        report.Details = sb.ToString();
        return report;
    }

    private static void AppendTypes(StringBuilder sb, Dictionary<(string Name, MemCat Cat), Acc> byType, MemCat cat, int n, string title)
    {
        sb.AppendLine();
        sb.AppendLine($"  {title} (by total size):");
        sb.AppendLine($"  {"Bytes",16} {"Count",12}  Type");
        foreach (Acc a in byType.Values.Where(a => a.Cat == cat).OrderByDescending(a => a.Bytes).Take(n))
            sb.AppendLine($"  {a.Bytes,16:N0} {a.Count,12:N0}  {a.Name}");
    }

    private static void AppendGroups(StringBuilder sb, Dictionary<(string Name, MemCat Cat), Acc> byType, MemCat cat, int n)
    {
        sb.AppendLine();
        sb.AppendLine("  By namespace:");
        sb.AppendLine($"  {"Bytes",16} {"Count",12}  Namespace");
        var groups = byType.Values.Where(a => a.Cat == cat)
            .GroupBy(a => a.Group)
            .Select(g => (Ns: g.Key, Bytes: g.Sum(a => a.Bytes), Count: g.Sum(a => a.Count)))
            .OrderByDescending(g => g.Bytes).Take(n);
        foreach (var g in groups)
            sb.AppendLine($"  {g.Bytes,16:N0} {g.Count,12:N0}  {g.Ns}");
    }
}
#endif
