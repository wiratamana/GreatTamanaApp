using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace GTA.Tools.Impl.IbmCloud
{
    public static partial class IbmCloudLogsApiClient
    {
        public static string InvestigateTrace(
            string                  keyword,
            long?                   eventTimeMsIn,
            int                     initialWindowMinutes,
            int                     threadBeforeSeconds,
            int                     threadAfterSeconds,
            int                     frequencyWindowHours,
            int                     pageSize,
            IbmCloudEnvironmentInfo env,
            int                     timeoutMs)
        {
            string logQueryUrl = LogQueryUrl(env.TenantId, env.Region);
            var report = new StringBuilder();
            report.AppendLine("════════════════════════════════════════════════════════════");
            report.AppendLine($" IBM CLOUD LOGS — ROOT-CAUSE INVESTIGATION: \"{keyword}\" [{env.Key}]");
            report.AppendLine("════════════════════════════════════════════════════════════");

            // STEP 1: initial keyword search
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long initStart, initEnd;
            if (eventTimeMsIn.HasValue)
            {
                initStart = eventTimeMsIn.Value - initialWindowMinutes * 60_000L;
                initEnd   = eventTimeMsIn.Value + initialWindowMinutes * 60_000L;
            }
            else
            {
                initEnd   = nowMs;
                initStart = nowMs - initialWindowMinutes * 60_000L;
            }

            string initBody = BuildQueryPayload(keyword, "exact", "Lucene",
                pageSize: Math.Min(pageSize, 100), pageIndex: 0, initStart, initEnd,
                wrapInQueryDef: true, ascending: false);

            IbmCloudApiResult initResult = Post(logQueryUrl, initBody, env, timeoutMs);

            report.AppendLine();
            report.AppendLine("── STEP 1: Initial keyword search ─────────────────────────────");
            report.AppendLine($"Window: {DateTimeOffset.FromUnixTimeMilliseconds(initStart):yyyy-MM-dd HH:mm:ss} → " +
                               $"{DateTimeOffset.FromUnixTimeMilliseconds(initEnd):yyyy-MM-dd HH:mm:ss} UTC");

            if (!initResult.Ok)
            {
                report.AppendLine(FormatError("initial search", initResult));
                return report.ToString().TrimEnd();
            }

            List<LogEntry> initEntries = ParseLogEntries(initResult.Body ?? "");
            report.AppendLine($"Found {initEntries.Count} matching entrie(s) (of {ExtractTotalCount(initResult.Body ?? "")} total).");

            if (initEntries.Count == 0)
            {
                report.AppendLine();
                report.AppendLine("⚠️ No matching log entries found in the initial window — cannot proceed to " +
                                   "thread-trace reconstruction. Try widening initial_window_minutes or supplying " +
                                   "a more precise event_time_ms.");
                return report.ToString().TrimEnd();
            }

            // Full listing — every matched entry, in full (no truncation, no count cap).
            foreach (LogEntry e in initEntries)
            {
                string tsStr = e.Timestamp > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds((long)e.Timestamp).ToString("yyyy-MM-dd HH:mm:ss.fff 'UTC'")
                    : "?";
                report.AppendLine($"  • {tsStr}  [{e.Severity}]  {e.Application}/{e.Subsystem}");
                report.AppendLine($"      {e.LogLine}");
            }

            // Anchor timestamp: caller-supplied, or newest match found
            long anchorMs = eventTimeMsIn ?? (long)initEntries.Max(e => e.Timestamp);

            // Extract thread name from whichever matched entry has one
            string? threadName = null;
            foreach (LogEntry e in initEntries)
            {
                threadName = ExtractThreadName(e.LogLine);
                if (threadName != null) break;
            }

            report.AppendLine();
            report.AppendLine($"Anchor timestamp : {DateTimeOffset.FromUnixTimeMilliseconds(anchorMs):yyyy-MM-dd HH:mm:ss.fff} UTC" +
                               (eventTimeMsIn.HasValue ? " (caller-supplied)" : " (newest match found)"));
            report.AppendLine($"Thread name found: {threadName ?? "(none — no '[...thread...]' pattern in matched log lines)"}");

            // STEP 2: narrow thread-trace reconstruction
            if (threadName != null)
            {
                long traceStart = anchorMs - threadBeforeSeconds * 1000L;
                long traceEnd   = anchorMs + threadAfterSeconds  * 1000L;

                string traceBody = BuildQueryPayload(threadName, "exact", "Lucene",
                    pageSize, pageIndex: 0, traceStart, traceEnd, wrapInQueryDef: true, ascending: true);

                IbmCloudApiResult traceResult = Post(logQueryUrl, traceBody, env, timeoutMs);

                report.AppendLine();
                report.AppendLine("── STEP 2: Thread-trace reconstruction (ascending) ─────────────");
                report.AppendLine($"Thread  : \"{threadName}\"");
                report.AppendLine($"Window  : {DateTimeOffset.FromUnixTimeMilliseconds(traceStart):yyyy-MM-dd HH:mm:ss.fff} → " +
                                   $"{DateTimeOffset.FromUnixTimeMilliseconds(traceEnd):yyyy-MM-dd HH:mm:ss.fff} UTC");

                if (!traceResult.Ok)
                {
                    report.AppendLine(FormatError("thread-trace query", traceResult));
                }
                else
                {
                    List<LogEntry> threadEntries = ParseLogEntries(traceResult.Body ?? "");
                    report.AppendLine($"Found {threadEntries.Count} log line(s) in this thread's lifecycle " +
                                       $"(of {ExtractTotalCount(traceResult.Body ?? "")} total):");

                    var seenApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (LogEntry e in threadEntries)
                    {
                        seenApps.Add($"{e.Application}/{e.Subsystem}");
                        string tsStr = e.Timestamp > 0
                            ? DateTimeOffset.FromUnixTimeMilliseconds((long)e.Timestamp).ToString("HH:mm:ss.fff")
                            : "?";
                        report.AppendLine();
                        report.AppendLine($"  [{tsStr}] [{e.Severity}] {e.Application}/{e.Subsystem}");
                        report.AppendLine($"    {e.LogLine}");
                    }

                    report.AppendLine();
                    report.AppendLine($"📌 Applications/subsystems seen in this trace (NOT filtered — the root " +
                                       $"cause is often in a different service than the caller): " +
                                       $"{string.Join(", ", seenApps)}");
                }
            }
            else
            {
                report.AppendLine();
                report.AppendLine("── STEP 2: SKIPPED (no thread name to trace) ────────────────────");
            }

            // STEP 3: company-wide frequency check
            long freqStart = anchorMs - frequencyWindowHours * 60 * 60_000L;
            long freqEnd   = anchorMs + frequencyWindowHours * 60 * 60_000L;

            string statsBody = BuildQueryPayload(keyword, "exact", "Lucene",
                pageSize: 1, pageIndex: 0, freqStart, freqEnd, wrapInQueryDef: false);

            IbmCloudApiResult statsResult = Post(LogQueryStatsUrl(env.TenantId, env.Region), statsBody, env, timeoutMs);

            report.AppendLine();
            report.AppendLine("── STEP 3: Company-wide frequency check ─────────────────────────");
            report.AppendLine($"Window: ±{frequencyWindowHours}h around anchor " +
                               $"({DateTimeOffset.FromUnixTimeMilliseconds(freqStart):yyyy-MM-dd HH:mm} → " +
                               $"{DateTimeOffset.FromUnixTimeMilliseconds(freqEnd):yyyy-MM-dd HH:mm} UTC)");

            if (!statsResult.Ok)
            {
                report.AppendLine(FormatError("frequency check", statsResult));
            }
            else
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(statsResult.Body ?? "{}");
                    long logsCount = doc.RootElement.TryGetProperty("logsCount", out var lc) ? lc.GetInt64() : 0;
                    report.AppendLine($"Occurrences of \"{keyword}\" in this window: {logsCount:N0}");
                    report.AppendLine(logsCount <= 3
                        ? "💡 Very rare in this window — points AWAY from a systemic/broad bug, more likely " +
                          "isolated to this one instance/record."
                        : "💡 Occurs multiple times in this window — worth checking whether other occurrences " +
                          "share the same input/record (systemic) or are all different (per-input issue).");
                }
                catch (Exception ex)
                {
                    report.AppendLine($"(could not parse frequency stats: {ex.Message})");
                }
            }

            report.AppendLine();
            report.AppendLine("════════════════════════════════════════════════════════════");
            report.AppendLine(" END OF INVESTIGATION REPORT");
            report.AppendLine("════════════════════════════════════════════════════════════");

            return report.ToString().TrimEnd();
        }
    }
}
