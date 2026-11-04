using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace GTA.Tools.Impl.IbmCloud
{
    public static partial class IbmCloudLogsApiClient
    {
        /// <summary>Where a resolved Coralogix token came from — purely descriptive (used in log/error text).</summary>
        public enum CoralogixTokenSource
        {
            EnvironmentVariable,
            Automated,
        }

        public readonly struct CoralogixAuth
        {
            public string               Token  { get; init; }
            public CoralogixTokenSource Source { get; init; }
        }

        private static string? ResolveEnvCoralogixToken(string region)
        {
            string regionEnvSuffix = region.Replace("-", "_").ToUpperInvariant();
            string? regional = Environment.GetEnvironmentVariable($"IBM_CORALOGIX_TOKEN_{regionEnvSuffix}");
            if (!string.IsNullOrWhiteSpace(regional))
                return regional;

            string? generic = Environment.GetEnvironmentVariable("IBM_CORALOGIX_TOKEN");
            if (!string.IsNullOrWhiteSpace(generic))
                return generic;

            string? legacy = Environment.GetEnvironmentVariable("IBM_QUERY_TOKEN");
            if (!string.IsNullOrWhiteSpace(legacy))
                return legacy;

            return null;
        }

        public static CoralogixAuth ResolveCoralogixAuth(IbmCloudEnvironmentInfo env)
        {
            string? envVal = ResolveEnvCoralogixToken(env.Region);
            if (envVal is not null)
                return new CoralogixAuth { Token = envVal, Source = CoralogixTokenSource.EnvironmentVariable };

            string auto = GetAutomatedCoralogixToken(env, forceRefresh: false);
            return new CoralogixAuth { Token = auto, Source = CoralogixTokenSource.Automated };
        }

        private static readonly Lazy<HttpClient> DirectHttpClient = new(() =>
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip
                                        | DecompressionMethods.Deflate
                                        | DecompressionMethods.Brotli,
                UseDefaultCredentials   = true,
                DefaultProxyCredentials = CredentialCache.DefaultCredentials,
            };
            return new HttpClient(handler);
        });

        private static IbmCloudApiResult ExecuteDirect(
            string                  method,
            string                  url,
            string?                 body,
            IbmCloudEnvironmentInfo env,
            string                  coralogixToken,
            CoralogixTokenSource    tokenSource,
            int                     timeoutMs)
        {
            IbmCloudApiResult result = ExecuteDirectAttempt(method, url, body, env.Region, env.TeamId, coralogixToken, timeoutMs);

            bool isAuthFailure = !result.Ok && (result.Status == 401 || result.Status == 403);
            if (!isAuthFailure)
                return result;

            string refreshedToken;
            try
            {
                refreshedToken = GetAutomatedCoralogixToken(env, forceRefresh: true);
            }
            catch (Exception refreshEx)
            {
                bool refreshWasTransient = IsTransientNetworkException(refreshEx);
                return IbmCloudApiResult.Failure(
                    BuildGiveUpMessage(result.Status,
                        $"the current 'coralogix_token' (source: {tokenSource}, environment: {env.Key}) was " +
                        $"rejected as invalid/expired, and automatically minting a fresh replacement ALSO " +
                        $"failed (after {AutoLoginMintMaxAttempts} internal retries) with " +
                        $"{(refreshWasTransient ? "a TRANSIENT NETWORK/TRANSPORT error" : "a NON-NETWORK error")}: " +
                        $"{FullExceptionMessage(refreshEx)}",
                        likelyTransient: refreshWasTransient),
                    status: result.Status, body: result.Body);
            }

            IbmCloudApiResult retryResult = ExecuteDirectAttempt(method, url, body, env.Region, env.TeamId, refreshedToken, timeoutMs);
            if (!retryResult.Ok && (retryResult.Status == 401 || retryResult.Status == 403))
            {
                return IbmCloudApiResult.Failure(
                    BuildGiveUpMessage(retryResult.Status,
                        $"the original 'coralogix_token' (source: {tokenSource}, environment: {env.Key}) was " +
                        "rejected, a fresh replacement was minted automatically for the SAME environment and " +
                        "tried too, and IBM Cloud Logs rejected THAT one as well",
                        likelyTransient: false),
                    status: retryResult.Status, body: retryResult.Body);
            }
            return retryResult;
        }

        private static string BuildGiveUpMessage(int status, string whatHappened, bool likelyTransient = false)
        {
            string explanation = likelyTransient
                ? "This looks like a TRANSIENT network/transport condition (e.g. a corporate-proxy connection " +
                  "reset or a DNS blip) rather than a broken IBM Cloud account or changed credentials — the " +
                  "same kind of transient error that has, on past occasions, gone away entirely on the very " +
                  "next unrelated call with no changes made."
                : "This indicates a genuine problem beyond a simple expired token (e.g. a revoked/locked IBM " +
                  "Cloud account, changed MailAccount/PrimaryAccount credentials in the Credentials folder, or " +
                  "an IBM Cloud Logs outage) that a token refresh cannot fix by itself.";

            string action = likelyTransient
                ? "ACTION SUGGESTED: this looks like a one-off network hiccup, not a broken account — it is " +
                  "reasonable to retry this exact ibm_cloud_logs_* call once more after a short pause. If it " +
                  "fails again with the same transient-network signature, stop retrying for the rest of this " +
                  "task, call the bug_report tool to log it (tool_name of the ibm_cloud_logs_* tool you " +
                  "called, this exact error message as evidence), and tell the user IBM Cloud Logs access may " +
                  "need investigation."
                : "ACTION REQUIRED: do NOT keep retrying this (or any other) ibm_cloud_logs_* tool call in " +
                  "this task — further attempts will very likely fail the same way. Instead: (1) call the " +
                  "bug_report tool now to log this failure (tool_name of the ibm_cloud_logs_* tool you called, " +
                  "this exact error message as evidence), and (2) tell the user that IBM Cloud Logs access " +
                  "needs human investigation before this tool can be used again.";

            return $"HTTP {status} — {whatHappened}. {explanation}\n\n{action}";
        }

        private static string FullExceptionMessage(Exception ex)
        {
            var parts = new System.Collections.Generic.List<string>();
            for (Exception? cur = ex; cur != null; cur = cur.InnerException)
                parts.Add(cur.Message);
            return string.Join(" ↳ ", parts);
        }

        private static IbmCloudApiResult ExecuteDirectAttempt(
            string method,
            string url,
            string? body,
            string region,
            string teamId,
            string coralogixToken,
            int timeoutMs)
        {
            HttpClient client = DirectHttpClient.Value;
            int effectiveTimeoutMs = timeoutMs > 0 ? timeoutMs : 30_000;

            for (int attempt = 1; attempt <= MaxRetries; attempt++)
            {
                try
                {
                    using var req = new HttpRequestMessage(new HttpMethod(method), url);
                    req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
                    req.Headers.TryAddWithoutValidation("cgx-team-id", teamId);
                    req.Headers.TryAddWithoutValidation("Origin", DashboardOrigin(region));
                    req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                    req.Headers.TryAddWithoutValidation("Cookie", $"prod-{region}.coralogix_token={coralogixToken}");

                    if (!string.IsNullOrEmpty(body))
                        req.Content = new StringContent(body, Encoding.UTF8, "application/json");

                    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(effectiveTimeoutMs));

                    using HttpResponseMessage resp = client.SendAsync(req, cts.Token)
                        .ConfigureAwait(false).GetAwaiter().GetResult();
                    string respBody = resp.Content.ReadAsStringAsync()
                        .ConfigureAwait(false).GetAwaiter().GetResult();
                    int status = (int)resp.StatusCode;

                    // Success
                    if (status >= 200 && status < 300)
                        return IbmCloudApiResult.Success(status, respBody);

                    return IbmCloudApiResult.Failure($"HTTP {status}", status: status, body: respBody);
                }
                catch (Exception ex) when (IsTransientNetworkException(ex))
                {
                    if (attempt == MaxRetries)
                    {
                        return IbmCloudApiResult.Failure(
                            $"Network error after {MaxRetries} attempt(s) via direct HttpClient: {ex.Message}");
                    }
                    Thread.Sleep(RetryBaseDelayMs * attempt);
                }
                catch (Exception ex)
                {
                    return IbmCloudApiResult.Failure($"Unexpected error (direct HttpClient path): {ex.Message}");
                }
            }

            return IbmCloudApiResult.Failure(
                $"Failed after {MaxRetries} retries (direct HttpClient path, no pipe involved): {url}");
        }

        private static bool IsTransientNetworkException(Exception ex)
        {
            switch (ex)
            {
                case OperationCanceledException:
                case HttpRequestException:
                case SocketException:
                    return true;
            }

            if (IsNetErr(ex.Message))
                return true;

            return ex.InnerException != null && IsTransientNetworkException(ex.InnerException);
        }

        private static bool IsAllowedTokenHost(string url, out string host)
        {
            host = "";
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
                return false;
            host = uri.Host.ToLowerInvariant();
            if (host.EndsWith(".coralogix.com") || host.EndsWith(".logs.cloud.ibm.com"))
                return true;
            foreach (string allowed in GTA.Core.InitSettings.Get("ibm_allowed_hosts", "")
                         .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (host == allowed.ToLowerInvariant() || host.EndsWith("." + allowed.TrimStart('.').ToLowerInvariant()))
                    return true;
            }
            return false;
        }

        public static IbmCloudApiResult Get(
            string                  url,
            IbmCloudEnvironmentInfo env,
            int                     timeoutMs = 30_000)
        {
            if (!IsAllowedTokenHost(url, out string blockedHost))
                return IbmCloudApiResult.Failure(
                    $"Blocked: host '{blockedHost}' is not an allowed IBM Cloud Logs host (https only; *.logs.cloud.ibm.com, " +
                    "*.coralogix.com or ini key ibm_allowed_hosts).");

            CoralogixAuth auth;
            try
            {
                auth = ResolveCoralogixAuth(env);
            }
            catch (Exception ex)
            {
                return IbmCloudApiResult.Failure(
                    $"Could not resolve/mint a Coralogix auth token: {ex.Message}");
            }
            return ExecuteDirect("GET", url, body: null, env: env,
                                  coralogixToken: auth.Token, tokenSource: auth.Source, timeoutMs: timeoutMs);
        }

        public static IbmCloudApiResult Post(
            string                  url,
            string?                 body,
            IbmCloudEnvironmentInfo env,
            int                     timeoutMs = 30_000)
        {
            if (!IsAllowedTokenHost(url, out string blockedHost))
                return IbmCloudApiResult.Failure(
                    $"Blocked: host '{blockedHost}' is not an allowed IBM Cloud Logs host (https only; *.logs.cloud.ibm.com, " +
                    "*.coralogix.com or ini key ibm_allowed_hosts).");

            CoralogixAuth auth;
            try
            {
                auth = ResolveCoralogixAuth(env);
            }
            catch (Exception ex)
            {
                return IbmCloudApiResult.Failure(
                    $"Could not resolve/mint a Coralogix auth token: {ex.Message}");
            }
            return ExecuteDirect("POST", url, body: body ?? "{}", env: env,
                                  coralogixToken: auth.Token, tokenSource: auth.Source, timeoutMs: timeoutMs);
        }
    }
}
