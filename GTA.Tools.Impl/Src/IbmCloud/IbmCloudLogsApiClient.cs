using System;
using System.Collections.Generic;

namespace GTA.Tools.Impl.IbmCloud
{
    public static partial class IbmCloudLogsApiClient
    {

        public const int MaxRetries       = 10;
        public const int RetryBaseDelayMs = 1_500;

        public static string ApiBase(string tenantId, string region)
            => $"https://{tenantId}.api.{region}.logs.cloud.ibm.com";

        public static string DashboardOrigin(string region)
            => $"https://dashboard.{region}.logs.cloud.ibm.com";

        public static string LogQueryUrl(string tenantId, string region)
            => $"{ApiBase(tenantId, region)}/api/v1/logquery";

        public static string LogQueryStatsUrl(string tenantId, string region)
            => $"{ApiBase(tenantId, region)}/api/v1/logquery/stats";

        public readonly record struct IbmCloudEnvironmentInfo(string Key, string TenantId, string Region, string TeamId);

        public static class IbmCloudEnvironments
        {
            public const string JpProd    = "jp-prod";
            public const string JpPreProd = "jp-preprod";
            public const string EuProd    = "eu-prod";
            public const string EuPreProd = "eu-preprod";

            /// <summary>Used whenever the 'environment' tool parameter is omitted.</summary>
            public const string Default = JpProd;

            private static IbmCloudEnvironmentInfo Env(string key, string iniPrefix, string region) =>
                new(key,
                    GTA.Core.InitSettings.Get(iniPrefix + "_tenant_id", ""),
                    region,
                    GTA.Core.InitSettings.Get(iniPrefix + "_team_id", ""));

            private static Dictionary<string, IbmCloudEnvironmentInfo> BuildMap() =>
                new(StringComparer.OrdinalIgnoreCase)
                {
                    [JpProd]    = Env(JpProd,    "ibm_jp_prod",    "jp-tok"),
                    [JpPreProd] = Env(JpPreProd, "ibm_jp_preprod", "jp-tok"),
                    [EuProd]    = Env(EuProd,    "ibm_eu_prod",    "eu-de"),
                    [EuPreProd] = Env(EuPreProd, "ibm_eu_preprod", "eu-de"),
                };

            /// <summary>All supported keys, comma-joined — used in error/help text.</summary>
            public static string SupportedListText => string.Join(", ", BuildMap().Keys);

            public static IbmCloudEnvironmentInfo Resolve(string? environmentKey)
            {
                string key = string.IsNullOrWhiteSpace(environmentKey) ? Default : environmentKey.Trim();
                if (BuildMap().TryGetValue(key, out var info)) return info;
                throw new ArgumentException(
                    $"Unsupported 'environment' value \"{environmentKey}\" — this tool only supports exactly " +
                    $"these 4 environments: {SupportedListText}.");
            }
        }

        private static readonly string[] NetErrorKeywords =
        {
            "強制的に切断",
            "forcibly closed",
            "接続はリモート",
            "An existing connection",
            "connection reset",
            "connection was reset",
            "ReceiveFailure",
            "SendFailure",
            "ConnectFailure",
            "Connection refused",
            "remote party closed",
            "The remote end closed",
            "SocketException",
            "Unable to read data from the transport",
        };

        public static bool IsNetErr(string? message)
        {
            if (string.IsNullOrEmpty(message)) return false;
            foreach (string kw in NetErrorKeywords)
                if (message.Contains(kw, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        public readonly struct IbmCloudApiResult
        {
            public bool    Ok     { get; init; }
            public int     Status { get; init; }
            public string? Body   { get; init; }
            public string? Error  { get; init; }

            public static IbmCloudApiResult Success(int status, string? body)
                => new() { Ok = true, Status = status, Body = body };

            public static IbmCloudApiResult Failure(string error, int status = 0, string? body = null)
                => new() { Ok = false, Status = status, Error = error, Body = body };
        }

        public static string FormatResponse(string label, IbmCloudApiResult result)
        {
            if (!result.Ok)
                return FormatError(label, result);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"✅ {label}");
            sb.AppendLine($"HTTP Status: {result.Status}");
            if (!string.IsNullOrEmpty(result.Body))
            {
                sb.AppendLine("--- Response Body ---");
                sb.AppendLine(result.Body);
            }
            return sb.ToString().TrimEnd();
        }

        public static string FormatError(string label, IbmCloudApiResult result)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"❌ {label}");
            sb.AppendLine($"Error: {result.Error}");
            if (result.Status > 0) sb.AppendLine($"HTTP Status: {result.Status}");
            if (!string.IsNullOrEmpty(result.Body))
            {
                sb.AppendLine("--- Response Body ---");
                sb.AppendLine(result.Body);
            }
            return sb.ToString().TrimEnd();
        }
    }
}
