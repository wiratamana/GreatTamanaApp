using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace GTA.Tools
{
    public sealed class FileViewLedger
    {
        private static readonly AsyncLocal<FileViewLedger?> _current = new();

        /// <summary>The ledger of the round currently executing tools, or null (no dedup).</summary>
        public static FileViewLedger? Current => _current.Value;

        public static IDisposable BeginScope()
        {
            FileViewLedger? previous = _current.Value;
            _current.Value = new FileViewLedger();
            return new Scope(previous);
        }

        private sealed class Scope : IDisposable
        {
            private readonly FileViewLedger? _previous;
            private bool _disposed;
            public Scope(FileViewLedger? previous) { _previous = previous; }
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _current.Value = _previous;
            }
        }

        private sealed class Entry
        {
            public long Length;
            public long MtimeTicks;
            public int  TotalLines;
            public readonly List<(int Start, int End, string Source)> Ranges = new();
        }

        private readonly object _gate = new();
        private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

        public void RecordShown(string path, int start, int end, int totalLines, string source, bool replaceExisting)
        {
            if (end < start || totalLines <= 0) return;
            if (!TryKey(path, out string key)) return;
            if (!TryFingerprint(key, out long length, out long ticks)) return;

            lock (_gate)
            {
                if (replaceExisting
                    || !_entries.TryGetValue(key, out Entry? entry)
                    || entry.Length != length
                    || entry.MtimeTicks != ticks)
                {
                    entry = new Entry { Length = length, MtimeTicks = ticks };
                    _entries[key] = entry;
                }

                entry.TotalLines = totalLines;
                entry.Ranges.Add((start, end, source));
            }
        }

        public bool TryGetCovering(string path, int start, int end, out string source)
        {
            source = string.Empty;
            if (!TryKey(path, out string key)) return false;

            lock (_gate)
            {
                if (!_entries.TryGetValue(key, out Entry? entry)) return false;

                if (!TryFingerprint(key, out long length, out long ticks)
                    || entry.Length != length
                    || entry.MtimeTicks != ticks)
                {
                    _entries.Remove(key); // file changed behind our back — forget everything
                    return false;
                }

                if (end < 0) end = entry.TotalLines - 1;
                if (start < 0 || start > end) return false;

                int     cursor = start;
                string? first  = null;
                while (cursor <= end)
                {
                    bool progressed = false;
                    foreach (var r in entry.Ranges)
                    {
                        if (r.Start <= cursor && cursor <= r.End)
                        {
                            first ??= r.Source;
                            cursor = r.End + 1;
                            progressed = true;
                        }
                    }
                    if (!progressed) return false; // a gap — the model has NOT seen this line
                }

                source = first ?? "an earlier tool call";
                return true;
            }
        }

        private static bool TryKey(string path, out string key)
        {
            key = string.Empty;
            if (string.IsNullOrWhiteSpace(path)) return false;
            try { key = Path.GetFullPath(path); return true; }
            catch { return false; }
        }

        private static bool TryFingerprint(string fullPath, out long length, out long ticks)
        {
            length = 0; ticks = 0;
            try
            {
                var fi = new FileInfo(fullPath);
                if (!fi.Exists) return false;
                length = fi.Length;
                ticks  = fi.LastWriteTimeUtc.Ticks;
                return true;
            }
            catch { return false; }
        }
    }
}
