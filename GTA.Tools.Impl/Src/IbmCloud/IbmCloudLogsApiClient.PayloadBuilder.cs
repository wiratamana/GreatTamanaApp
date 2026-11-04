using System;
using System.Text.Json.Nodes;

namespace GTA.Tools.Impl.IbmCloud
{
    public static partial class IbmCloudLogsApiClient
    {
        public static string BuildQueryPayload(
            string queryText,
            string queryType,
            string syntax,
            int    pageSize,
            int    pageIndex,
            long   startMs,
            long   endMs,
            bool   wrapInQueryDef,
            bool   ascending = false)
        {
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var inner = new JsonObject
            {
                ["type"]     = "freeText",
                ["pageSize"] = pageSize,
                ["queryParams"] = new JsonObject
                {
                    ["query"] = new JsonObject
                    {
                        ["text"]        = queryText,
                        ["type"]        = queryType,
                        ["syntax"]      = syntax,
                        ["templateIds"] = new JsonArray()
                    },
                    ["templateIds"] = new JsonArray(),
                    ["metadata"] = new JsonObject
                    {
                        ["applicationName"] = new JsonArray(),
                        ["subsystemName"]   = new JsonArray(),
                        ["operationName"]   = new JsonArray(),
                        ["serviceName"]     = new JsonArray(),
                        ["priorityclass"]   = new JsonArray(),
                        ["severity"]        = new JsonArray()
                    },
                    ["jsonObject"]          = new JsonObject(),
                    ["jsonAggFields"]       = new JsonArray(),
                    ["ts_agg"]              = null,
                    ["aggregationInterval"] = 60000,
                    ["externalFilters"] = new JsonObject
                    {
                        ["teams"]                   = new JsonArray(),
                        ["templateClassification"]  = new JsonArray()
                    },
                    ["selectedLogs"]      = new JsonArray(),
                    ["customParam"]       = new JsonObject(),
                    ["archiveFilterPath"] = new JsonObject()
                },
                ["sortModel"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["field"]    = "timestamp",
                        ["ordering"] = ascending ? "asc" : "desc",
                        ["missing"]  = "_last"
                    }
                },
                ["graphs"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"]         = "Histogram",
                        ["startDate"]    = startMs,
                        ["endDate"]      = endMs,
                        ["aggField"]     = "coralogix.metadata.severity",
                        ["aggFieldPath"] = new JsonArray { "coralogix", "metadata", "severity" },
                        ["aggBy"]        = new JsonObject { ["type"] = "count" },
                        ["fieldType"]    = "log"
                    }
                },
                ["persistent"]                      = false,
                ["selectedQuerySyntax"]              = syntax.ToLowerInvariant(),
                ["nowDate"]                          = nowMs,
                ["cacheQueryId"]                     = GenerateCacheQueryId(),
                ["shouldAggregateGeneralTemplates"]  = true,
                ["endDate"]                          = endMs,
                ["startDate"]                        = startMs,
                ["tagId"]                            = -1,
                ["selectedViewId"]                   = -1,
                ["pageIndex"]                        = pageIndex,
                ["selectedSurroundLog"] = new JsonObject
                {
                    ["id"]        = null,
                    ["timestamp"] = null,
                    ["limit"]     = 100
                }
            };

            JsonObject payload = wrapInQueryDef ? new JsonObject { ["queryDef"] = inner } : inner;
            return payload.ToJsonString();
        }

        private static string GenerateCacheQueryId()
        {
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
            var rng = Random.Shared;
            Span<char> buf = stackalloc char[21];
            for (int i = 0; i < buf.Length; i++) buf[i] = chars[rng.Next(chars.Length)];
            return new string(buf);
        }
    }
}
