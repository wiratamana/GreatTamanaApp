using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace GTA.Core
{
    /// <summary>Per-model USD pricing, expressed as dollars per ONE MILLION tokens (MTok) — matches the units vendors publish</summary>
    public readonly record struct ModelPriceEntry(
        double InputPerMTok,
        double OutputPerMTok,
        double CacheWritePerMTok,
        double CacheReadPerMTok);

    public static class ModelPricing
    {
        private static readonly object _lock = new();
        private static Dictionary<string, ModelPriceEntry> _cache =
            new(StringComparer.OrdinalIgnoreCase);
        private static DateTime _cacheFileWriteTimeUtc = DateTime.MinValue;
        private static string?  _cachedPath;

        /// <summary>Location of the editable pricing file.</summary>
        private static string ConfigPath =>
            Path.Combine(AppContext.BaseDirectory, "Config", "model_pricing.ini");

        /// <summary>Look up the pricing entry for <paramref name="modelName"/>.</summary>
        public static bool TryGetPrice(string? modelName, out ModelPriceEntry entry)
        {
            entry = default;
            if (string.IsNullOrWhiteSpace(modelName)) return false;

            var table = LoadTable();
            return table.TryGetValue(modelName.Trim(), out entry);
        }

        /// <summary>Compute the USD cost of one <see cref="TokenUsage"/> snapshot for <paramref name="modelName"/>.</summary>
        public static bool TryComputeCost(string? modelName, TokenUsage usage, out double costUsd)
        {
            costUsd = 0;
            if (!TryGetPrice(modelName, out ModelPriceEntry p)) return false;

            costUsd =
                usage.InputTokens              / 1_000_000.0 * p.InputPerMTok      +
                usage.OutputTokens             / 1_000_000.0 * p.OutputPerMTok     +
                usage.CacheCreationInputTokens / 1_000_000.0 * p.CacheWritePerMTok  +
                usage.CacheReadInputTokens     / 1_000_000.0 * p.CacheReadPerMTok;
            return true;
        }

        // ── File loading / caching ────────────────────────────────────────────

        private static Dictionary<string, ModelPriceEntry> LoadTable()
        {
            lock (_lock)
            {
                string path = ConfigPath;

                if (!File.Exists(path))
                {
                    _cache                 = new(StringComparer.OrdinalIgnoreCase);
                    _cachedPath            = path;
                    _cacheFileWriteTimeUtc = DateTime.MinValue;
                    return _cache;
                }

                DateTime writeTime = File.GetLastWriteTimeUtc(path);
                if (_cachedPath == path && writeTime == _cacheFileWriteTimeUtc)
                    return _cache; // unchanged since last parse — skip re-parsing

                try
                {
                    _cache = ParseIni(File.ReadAllLines(path));
                }
                catch
                {
                    // Never let a malformed pricing file break the chat —
                    // just fall back to "no pricing known" (N/A everywhere).
                    _cache = new(StringComparer.OrdinalIgnoreCase);
                }

                _cachedPath            = path;
                _cacheFileWriteTimeUtc = writeTime;
                return _cache;
            }
        }

        private static Dictionary<string, ModelPriceEntry> ParseIni(string[] lines)
        {
            var result = new Dictionary<string, ModelPriceEntry>(StringComparer.OrdinalIgnoreCase);

            string? currentSection = null;
            double input = 0, output = 0, cacheWrite = 0, cacheRead = 0;

            void Flush()
            {
                if (currentSection != null)
                    result[currentSection] = new ModelPriceEntry(input, output, cacheWrite, cacheRead);
            }

            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
                    continue;

                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    Flush();
                    currentSection = line[1..^1].Trim();
                    input = output = cacheWrite = cacheRead = 0;
                    continue;
                }

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;

                string key    = line[..eq].Trim();
                string valStr = line[(eq + 1)..].Trim();

                int commentIdx = valStr.IndexOfAny(new[] { ';', '#' });
                if (commentIdx >= 0) valStr = valStr[..commentIdx].Trim();

                if (!double.TryParse(valStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double val))
                    continue;

                switch (key.ToLowerInvariant())
                {
                    case "input":      input      = val; break;
                    case "output":     output     = val; break;
                    case "cachewrite": cacheWrite = val; break;
                    case "cacheread":  cacheRead  = val; break;
                }
            }

            Flush();
            return result;
        }
    }
}
