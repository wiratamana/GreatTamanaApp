using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GTA.Tools.Impl.IbmCloud
{
    public static partial class IbmCloudLogsApiClient
    {
        /// <summary>One parsed entry from a /api/v1/logquery response's "logs" array.</summary>
        public readonly record struct LogEntry(
            double Timestamp, string Severity, string Application, string Subsystem, string LogLine);

        public static List<LogEntry> ParseLogEntries(string responseBody)
        {
            var results = new List<LogEntry>();
            try
            {
                JsonNode? node = JsonNode.Parse(responseBody);
                JsonArray? logs = node?["logs"]?.AsArray();
                if (logs == null) return results;

                foreach (JsonNode? entry in logs)
                {
                    if (entry == null) continue;

                    double ts = 0;
                    try { ts = entry["timestamp"]?.GetValue<double>() ?? 0; } catch { }

                    string severity    = SafeString(entry["metadata"]?["severity"]);
                    string application = SafeString(entry["metadata"]?["applicationName"]);
                    string subsystem   = SafeString(entry["metadata"]?["subsystemName"]);

                    string rawText = SafeString(entry["text"]);
                    string logLine = rawText;
                    try
                    {
                        JsonNode? inner = JsonNode.Parse(rawText);
                        string? l = inner?["log"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(l)) logLine = l;
                    }
                    catch { /* text wasn't nested JSON — use as-is (enterprise logs) */ }

                    results.Add(new LogEntry(ts, severity, application, subsystem, logLine));
                }
            }
            catch { /* malformed body — return whatever was parsed so far */ }

            return results;
        }

        private static string SafeString(JsonNode? node)
        {
            if (node == null) return "?";
            try { return node.GetValue<string>(); } catch { return node.ToString(); }
        }

        public static int ExtractTotalCount(string responseBody)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(responseBody);
                if (doc.RootElement.TryGetProperty("total", out JsonElement totalEl) &&
                    totalEl.ValueKind == JsonValueKind.Number)
                    return totalEl.GetInt32();
            }
            catch { }
            return 0;
        }

        public static string FormatLogEntries(string responseBody, string label)
        {
            var sb = new StringBuilder();
            int total = ExtractTotalCount(responseBody);
            List<LogEntry> entries = ParseLogEntries(responseBody);

            sb.AppendLine($"✅ {label}");
            sb.AppendLine($"Matched {total} total log entrie(s); showing all {entries.Count} returned by IBM Cloud Logs.");

            if (entries.Count == 0)
            {
                sb.AppendLine("(no log entries in response)");
                return sb.ToString().TrimEnd();
            }

            int n = 1;
            foreach (LogEntry e in entries)
            {
                string tsStr = e.Timestamp > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds((long)e.Timestamp).ToString("yyyy-MM-dd HH:mm:ss.fff 'UTC'")
                    : "?";

                sb.AppendLine();
                sb.AppendLine($"[{n}] {tsStr}  [{e.Severity}]  {e.Application}/{e.Subsystem}");
                sb.AppendLine($"    {e.LogLine}");
                n++;
            }

            return sb.ToString().TrimEnd();
        }

        public static string PrettyPrintJson(string rawJson)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(rawJson);
                return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
            }
            catch
            {
                return rawJson; // fall back to raw text if it doesn't parse
            }
        }

        private static readonly Regex ThreadNamePattern =
            new(@"\[([^\]]*[Tt]hread[^\]]*)\]", RegexOptions.Compiled);

        public static string? ExtractThreadName(string logLine)
        {
            if (string.IsNullOrEmpty(logLine)) return null;
            Match m = ThreadNamePattern.Match(logLine);
            return m.Success ? m.Groups[1].Value : null;
        }

        private static readonly Regex JwtPattern =
            new(@"eyJ[A-Za-z0-9_\-]+\.eyJ[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+", RegexOptions.Compiled);

        private static readonly Regex CookieHeaderPattern =
            new(@"(Cookie=\[)[^\]]*(\])", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex CookieHeaderPattern2 =
            new(@"(Cookie\\?""?\s*[:=]\s*\\?""?)[^,\}\""]{20,}", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex NamedCookiePattern = new(
            @"(JSESSIONID|WASOidcSession|\w*LtpaToken2|com\.ibm\.cloud\.iam\.[\w.]+|" +
            @"prod-[\w-]+\.coralogix_token|CISESSIONIDPR\w*|BMAID|ak_bmsc|bm_sv|bm_sz|_abck|" +
            @"mbox|utag_main|_mkto_trk|_clck|_clsk|ajs_\w+|OPTOUTMULTI|notice_\w+|" +
            @"pageviewContext|kndctr_\w+|AMCV_\w+|_ga\w*|_uet\w*)=([^;\""'\\,\s]{6,})",
            RegexOptions.Compiled);

        private static readonly Regex EnvoyMetadataPattern =
            new(@"x-envoy-peer-metadata(-id)?=\[?[A-Za-z0-9+/=]{60,}\]?", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Deliberately broad — redacts ALL dotted-quads (internal or client IPs).
        private static readonly Regex Ipv4Pattern =
            new(@"\b(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)\b", RegexOptions.Compiled);

        public static string RedactSensitiveData(string text)
        {
            string result = text;
            result = JwtPattern.Replace(result, "[REDACTED_JWT]");
            result = CookieHeaderPattern.Replace(result, "$1[REDACTED_COOKIE]$2");
            result = CookieHeaderPattern2.Replace(result, "$1[REDACTED_COOKIE]");
            result = NamedCookiePattern.Replace(result, "$1=[REDACTED_COOKIE]");
            result = EnvoyMetadataPattern.Replace(result, "[REDACTED_ENVOY_METADATA]");
            result = Ipv4Pattern.Replace(result, "[REDACTED_IP]");
            return result;
        }
    }
}
