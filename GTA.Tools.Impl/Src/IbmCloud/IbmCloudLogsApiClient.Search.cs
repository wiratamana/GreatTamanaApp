using System;

namespace GTA.Tools.Impl.IbmCloud
{
    public static partial class IbmCloudLogsApiClient
    {
        public static string SearchLogs(
            string                  query,
            long?                   startMsIn,
            long?                   endMsIn,
            int                     minutesBack,
            string                  queryType,
            string                  syntax,
            int                     pageSize,
            int                     pageIndex,
            IbmCloudEnvironmentInfo env,
            int                     timeoutMs)
        {
            // Empty query → wildcard "browse everything in this window" UX.
            if (string.IsNullOrWhiteSpace(query))
            {
                query     = "*";
                queryType = "wildcard";
            }

            long endMs   = endMsIn   ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long startMs = startMsIn ?? (endMs - minutesBack * 60_000L);

            string body = BuildQueryPayload(query, queryType, syntax, pageSize, pageIndex,
                startMs, endMs, wrapInQueryDef: true);

            string url = LogQueryUrl(env.TenantId, env.Region);
            IbmCloudApiResult result = Post(url, body, env, timeoutMs);

            if (!result.Ok)
                return FormatError($"ibm_cloud_logs_search (query=\"{query}\", environment=\"{env.Key}\")", result);

            string label = $"ibm_cloud_logs_search — query=\"{query}\" [{env.Key}] " +
                            $"[{DateTimeOffset.FromUnixTimeMilliseconds(startMs):yyyy-MM-dd HH:mm:ss} → " +
                            $"{DateTimeOffset.FromUnixTimeMilliseconds(endMs):yyyy-MM-dd HH:mm:ss} UTC]";
            return FormatLogEntries(result.Body ?? "", label);
        }
    }
}
