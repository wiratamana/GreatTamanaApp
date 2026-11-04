using System;
using System.Text.Json;

namespace GTA.Tools.Impl.IbmCloud
{
    public static partial class IbmCloudLogsApiClient
    {
        public static string GetStats(
            string                  query,
            long?                   startMsIn,
            long?                   endMsIn,
            int                     minutesBack,
            string                  queryType,
            string                  syntax,
            IbmCloudEnvironmentInfo env,
            int                     timeoutMs)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                query     = "*";
                queryType = "wildcard";
            }

            long endMs   = endMsIn   ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long startMs = startMsIn ?? (endMs - minutesBack * 60_000L);

            // /logquery/stats — body is the SAME inner object UNWRAPPED (no "queryDef").
            string body = BuildQueryPayload(query, queryType, syntax, pageSize: 1, pageIndex: 0,
                startMs, endMs, wrapInQueryDef: false);

            string url = LogQueryStatsUrl(env.TenantId, env.Region);
            IbmCloudApiResult result = Post(url, body, env, timeoutMs);

            if (!result.Ok)
                return FormatError($"ibm_cloud_logs_stats (query=\"{query}\", environment=\"{env.Key}\")", result);

            return FormatStatsResponse(result.Body ?? "", query, startMs, endMs);
        }

        private static string FormatStatsResponse(string body, string query, long startMs, long endMs)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"✅ ibm_cloud_logs_stats — query=\"{query}\" " +
                          $"[{DateTimeOffset.FromUnixTimeMilliseconds(startMs):yyyy-MM-dd HH:mm:ss} → " +
                          $"{DateTimeOffset.FromUnixTimeMilliseconds(endMs):yyyy-MM-dd HH:mm:ss} UTC]");

            try
            {
                using JsonDocument doc = JsonDocument.Parse(body);
                JsonElement root = doc.RootElement;
                long logsCount     = root.TryGetProperty("logsCount", out var lc) ? lc.GetInt64() : 0;
                long templateCount = root.TryGetProperty("templateCount", out var tc) ? tc.GetInt64() : 0;
                long mappingExcCount = root.TryGetProperty("logCountMappingExceptionCount", out var mc) ? mc.GetInt64() : 0;

                sb.AppendLine($"logsCount     : {logsCount:N0}");
                sb.AppendLine($"templateCount : {templateCount:N0}");
                if (mappingExcCount > 0)
                    sb.AppendLine($"logCountMappingExceptionCount : {mappingExcCount:N0}");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"(could not parse stats response: {ex.Message})");
                sb.AppendLine("--- Raw Response Body ---");
                sb.AppendLine(body);
            }

            return sb.ToString().TrimEnd();
        }
    }
}
