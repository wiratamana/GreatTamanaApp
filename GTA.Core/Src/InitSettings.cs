using System;
using System.Collections.Generic;
using System.IO;

namespace GTA.Core
{
    /// <summary>Reads Config\init.ini (plus optional Config\secret.ini, which wins). Flat "key = value" lines, live-reloaded.</summary>
    public static class InitSettings
    {
        private static readonly object _lock = new();
        private static Dictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);
        private static long _stamp = -1;

        private static string ConfigDir => Path.Combine(AppContext.BaseDirectory, "Config");

        public static string Get(string key, string fallback = "")
            => LoadTable().TryGetValue(key, out string? value) ? value : fallback;

        public static bool TryGet(string key, out string value)
            => LoadTable().TryGetValue(key, out value!);

        private static Dictionary<string, string> LoadTable()
        {
            lock (_lock)
            {
                string initPath = Path.Combine(ConfigDir, "init.ini");
                string secretPath = Path.Combine(ConfigDir, "secret.ini");

                long stamp = Stamp(initPath) * 31 + Stamp(secretPath);
#if CO_WORKER_USE
                // Encrypted Credentials\my_credentials.dat wins over the ini files (see SecureCredentialStore).
                stamp = stamp * 31 + SecureCredentialStore.Version;
#endif
                if (stamp == _stamp) return _cache;

                var table = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                Merge(table, initPath);
                Merge(table, secretPath);
#if CO_WORKER_USE
                foreach (var kv in SecureCredentialStore.Snapshot())
                {
                    // An empty value in the .dat (credential not entered yet) must not hide a value from the ini files.
                    if (kv.Value.Length > 0 || !table.ContainsKey(kv.Key))
                        table[kv.Key] = kv.Value;
                }
#endif
                _cache = table;
                _stamp = stamp;
                return _cache;
            }
        }

        private static long Stamp(string path)
        {
            try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0; }
            catch { return 0; }
        }

        private static void Merge(Dictionary<string, string> table, string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                foreach (var kv in ParseFlatIni(File.ReadAllLines(path)))
                    table[kv.Key] = kv.Value;
            }
            catch
            {
                // a malformed file must never break the app
            }
        }

        private static Dictionary<string, string> ParseFlatIni(string[] lines)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#')
                    continue;

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;

                string key = line[..eq].Trim();
                result[key] = ParseValue(line[(eq + 1)..].Trim());
            }

            return result;
        }

        private static string ParseValue(string raw)
        {
            if (raw.Length == 0 || raw[0] == ';' || raw[0] == '#') return "";

            if (raw[0] == '"')
            {
                int close = raw.IndexOf('"', 1);
                if (close > 0) return raw[1..close];
            }

            for (int i = 0; i < raw.Length; i++)
            {
                if ((raw[i] == ';' || raw[i] == '#') && i > 0 && char.IsWhiteSpace(raw[i - 1]))
                    return raw[..i].Trim();
            }
            return raw;
        }
    }
}
