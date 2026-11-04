using System;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GTA.Tools.Impl.IbmCloud
{
    public static partial class IbmCloudLogsApiClient
    {
        public static string ExportEvidence(
            string                  queriesJson,
            string                  outputDir,
            bool                    redact,
            int                     pageSize,
            IbmCloudEnvironmentInfo env,
            int                     timeoutMs)
        {
            if (string.IsNullOrWhiteSpace(queriesJson))
                return "Error: 'queries' is required (JSON array of {label, keyword, start_ms, end_ms}).";
            if (string.IsNullOrWhiteSpace(outputDir))
                return "Error: 'output_dir' is required.";

            JsonArray? queryDefs;
            try
            {
                queryDefs = JsonNode.Parse(queriesJson)?.AsArray();
            }
            catch (Exception ex)
            {
                return $"Error: 'queries' is not valid JSON: {ex.Message}";
            }

            if (queryDefs == null || queryDefs.Count == 0)
                return "Error: 'queries' parsed to an empty/invalid array.";

            Directory.CreateDirectory(outputDir);

            var manifest = new StringBuilder();
            manifest.AppendLine("IBM Cloud Logs — Raw Evidence Export Manifest");
            manifest.AppendLine("================================================================");
            manifest.AppendLine();
            manifest.AppendLine("Every file in this folder is the JSON response body returned by the");
            manifest.AppendLine("IBM Cloud Logs API (POST /api/v1/logquery) for the query described below,");
            manifest.AppendLine("pretty-printed (indented) for readability.");
            manifest.AppendLine();
            if (redact)
            {
                manifest.AppendLine("MODE: REDACTED. Diagnostic fields (IDs, timestamps, error codes & messages,");
                manifest.AppendLine("endpoint paths, request/response bodies, thread IDs) are preserved exactly.");
                manifest.AppendLine("The following value TYPES were replaced with a clear marker:");
                manifest.AppendLine("  [REDACTED_JWT]              - JWT bearer tokens");
                manifest.AppendLine("  [REDACTED_COOKIE]           - Cookie header values / session cookies");
                manifest.AppendLine("  [REDACTED_IP]                - IPv4 addresses (internal and client)");
                manifest.AppendLine("  [REDACTED_ENVOY_METADATA]   - Envoy/Istio peer-metadata base64 blobs");
                manifest.AppendLine();
                manifest.AppendLine("Re-run with redact=false for the unredacted original if specifically needed.");
            }
            else
            {
                manifest.AppendLine("MODE: RAW / UNREDACTED. Nothing was removed or altered — this is the exact");
                manifest.AppendLine("original API response (pretty-printed only). May contain sensitive material");
                manifest.AppendLine("(tokens, cookies, IPs) — handle accordingly before sharing further.");
            }
            manifest.AppendLine();
            manifest.AppendLine($"Log source: environment {env.Key} (tenant {env.TenantId}, region {env.Region}, team {env.TeamId})");
            manifest.AppendLine($"Generated : {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            manifest.AppendLine();

            var summary = new StringBuilder();
            summary.AppendLine($"✅ ibm_cloud_logs_export_evidence — {queryDefs.Count} quer(ies) requested [{env.Key}]");
            summary.AppendLine($"Output folder: {outputDir}");
            summary.AppendLine($"Mode: {(redact ? "REDACTED" : "RAW / UNREDACTED")}");
            summary.AppendLine();

            string logQueryUrl = LogQueryUrl(env.TenantId, env.Region);
            int fileNum = 1;

            foreach (JsonNode? qNode in queryDefs)
            {
                if (qNode == null) continue;
                if (qNode is not JsonObject qObj) continue;

                string label   = qObj["label"]?.ToString() ?? $"query_{fileNum}";
                string keyword = qObj["keyword"]?.ToString() ?? "*";
                long   startMs = TryGetLongFromNode(qObj["start_ms"]) ??
                                 (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 15 * 60_000L);
                long   endMs   = TryGetLongFromNode(qObj["end_ms"]) ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                string fileName = $"{fileNum:D2}_{SanitizeFileName(label)}.json";

                string body = BuildQueryPayload(keyword, "exact", "Lucene", pageSize, pageIndex: 0,
                    startMs, endMs, wrapInQueryDef: true, ascending: true);

                IbmCloudApiResult result = Post(logQueryUrl, body, env, timeoutMs);

                manifest.AppendLine("----------------------------------------------------------------");
                manifest.AppendLine($"File        : {fileName}");
                manifest.AppendLine($"Label       : {label}");
                manifest.AppendLine($"Keyword     : {keyword}");
                manifest.AppendLine($"Window      : {DateTimeOffset.FromUnixTimeMilliseconds(startMs):yyyy-MM-dd HH:mm:ss} → " +
                                     $"{DateTimeOffset.FromUnixTimeMilliseconds(endMs):yyyy-MM-dd HH:mm:ss} UTC");
                manifest.AppendLine($"HTTP Status : {(result.Ok ? result.Status.ToString() : $"FAILED — {result.Error}")}");
                manifest.AppendLine();

                if (result.Ok && !string.IsNullOrWhiteSpace(result.Body))
                {
                    string pretty = PrettyPrintJson(result.Body);
                    string output = redact ? RedactSensitiveData(pretty) : pretty;
                    string path   = Path.Combine(outputDir, fileName);
                    File.WriteAllText(path, output, Encoding.UTF8);

                    int entryCount = ParseLogEntries(result.Body).Count;
                    summary.AppendLine($"[{fileNum}/{queryDefs.Count}] \"{label}\" → {entryCount} entrie(s) saved to {fileName}");
                }
                else
                {
                    summary.AppendLine($"[{fileNum}/{queryDefs.Count}] \"{label}\" → FAILED: {result.Error}");
                }

                fileNum++;
            }

            string manifestPath = Path.Combine(outputDir, "00_README_query_definitions.txt");
            File.WriteAllText(manifestPath, manifest.ToString(), Encoding.UTF8);
            summary.AppendLine();
            summary.AppendLine($"Manifest written → {manifestPath}");

            return summary.ToString().TrimEnd();
        }

        private static long? TryGetLongFromNode(JsonNode? node)
        {
            if (node is null) return null;
            try
            {
                if (node is JsonValue jv)
                {
                    if (jv.TryGetValue<long>(out long l)) return l;
                    if (jv.TryGetValue<double>(out double d)) return (long)d;
                    if (jv.TryGetValue<string>(out string? s) && long.TryParse(s, out long parsed)) return parsed;
                }
            }
            catch { }
            return null;
        }

        private static string SanitizeFileName(string label)
        {
            string cleaned = Regex.Replace(label, @"[^A-Za-z0-9_\-]+", "_").Trim('_');
            return string.IsNullOrEmpty(cleaned) ? "query" : cleaned.Length > 60 ? cleaned.Substring(0, 60) : cleaned;
        }
    }
}
