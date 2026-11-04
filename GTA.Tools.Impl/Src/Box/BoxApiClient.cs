using System.Text.RegularExpressions;

namespace GTA.Tools.Impl.Box
{
    public static class BoxApiClient
    {

        public const int MaxRetries       = 10;
        public const int RetryBaseDelayMs = 1_500;

        public static string BoxHost      => GTA.Core.InitSettings.Get("box_enterprise_host");
        public static string AccountHost  => GTA.Core.InitSettings.Get("box_account_host");
        public static string InternalBase => "https://" + BoxHost + "/app-api/enduserapp";

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
            "timed out",
        };

        public readonly struct BoxApiResult
        {
            public bool    Ok       { get; init; }
            public int     Status   { get; init; }
            public string? Body     { get; init; }
            public string? Error    { get; init; }

            public static BoxApiResult Success(int status, string? body)
                => new() { Ok = true, Status = status, Body = body };

            public static BoxApiResult Failure(string error, int status = 0, string? body = null)
                => new() { Ok = false, Status = status, Error = error, Body = body };
        }

        public static BoxApiResult Get(
            string  url,
            string? bearer    = null,
            int     timeoutMs = 30_000)
            => Execute("GET", url, body: null, bearer: bearer, timeoutMs: timeoutMs);

        public static BoxApiResult Post(
            string  url,
            string? body      = null,
            string? bearer    = null,
            int     timeoutMs = 30_000)
            => Execute("POST", url, body: body ?? "{}", bearer: bearer, timeoutMs: timeoutMs);

        private static BoxApiResult Execute(
            string  method,
            string  url,
            string? body,
            string? bearer,
            int     timeoutMs)
        {
            bool useBearerAuth = !string.IsNullOrEmpty(bearer);
            int  authRefreshes = 0;

            for (int attempt = 1; attempt <= MaxRetries; attempt++)
            {
                BoxAdfsLoginClient client = useBearerAuth
                    ? BoxSession.GetClient()
                    : BoxSession.GetLoggedInClient(forceRelogin: authRefreshes > 0);

                var (status, respBody, error) = client
                    .ExecuteAsync(method, url, body, bearer, timeoutMs)
                    .ConfigureAwait(false).GetAwaiter().GetResult();

                if (error != null)
                {
                    if (IsNetErr(error) && attempt < MaxRetries)
                    {
                        Thread.Sleep(RetryBaseDelayMs * attempt);
                        continue;
                    }
                    return BoxApiResult.Failure(
                        $"Network error (attempt {attempt}/{MaxRetries}): {error}");
                }

                if ((status == 401 || status == 403 || status == 302) && !useBearerAuth && authRefreshes == 0)
                {
                    authRefreshes++;
                    continue;
                }

                if (status >= 200 && status < 300)
                    return BoxApiResult.Success(status, respBody);

                return BoxApiResult.Failure($"HTTP {status}", status: status, body: respBody);
            }

            return BoxApiResult.Failure(
                $"Failed after {MaxRetries} retries — network issue calling: {url}");
        }

        public static void EnsureBoxTabActive()
        {
            try { BoxSession.GetLoggedInClient(); }
            catch { }
        }

        private static bool IsAuthError(string message) =>
            message.Contains("401") ||
            message.Contains("403") ||
            message.Contains("Not logged in", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("csrf-token", StringComparison.OrdinalIgnoreCase);

        private static string BuildDownloadErrorHint(string? error)
        {
            if (error == null || !error.Contains("403")) return "";
            return " [HINT: this usually means the file is only accessible to this account via a " +
                   "shared link, not direct folder membership. Re-call this tool with a " +
                   "shared_link_url parameter set to the original Box shared link " +
                   "(https://<tenant>.box.com/s/<token>), or locate the same file_id via box_search " +
                   "under normal folder access.]";
        }

        public static (string? Base64, string? Error) FetchBase64(
            string  fileId,
            int     timeoutMs,
            string? sharedLinkUrl = null)
        {
            var (bytes, _, _, error) = DownloadFileBytes(fileId, timeoutMs, sharedLinkUrl);
            if (error != null) return (null, error);
            if (bytes == null || bytes.Length == 0) return (null, "Empty file data.");
            return (Convert.ToBase64String(bytes), null);
        }

        public static (string? Base64, string? ResolvedFileId, string? Error) FetchBase64ViaSharedLink(
            string  sharedToken,
            string? knownFileId,
            int     timeoutMs)
        {
            string? lastError     = null;
            string? resolvedFileId = knownFileId;

            for (int attempt = 1; attempt <= MaxRetries; attempt++)
            {
                var client = BoxSession.GetLoggedInClient(forceRelogin: lastError != null && IsAuthError(lastError));

                var (bytes, _, fid, error) = client
                    .DownloadFileViaSharedLinkAsync(sharedToken, knownFileId, timeoutMs)
                    .ConfigureAwait(false).GetAwaiter().GetResult();

                resolvedFileId = fid ?? resolvedFileId;

                if (error == null)
                {
                    if (bytes == null || bytes.Length == 0)
                        return (null, resolvedFileId, "Empty file data.");
                    return (Convert.ToBase64String(bytes), resolvedFileId, null);
                }

                lastError = error;
                bool netErr  = IsNetErr(error);
                bool authErr = IsAuthError(error);

                if ((netErr || authErr) && attempt < MaxRetries)
                {
                    if (netErr) Thread.Sleep(RetryBaseDelayMs * attempt);
                    continue;
                }

                break;
            }

            return (null, resolvedFileId, lastError ?? "Unknown download error");
        }

        public static (byte[]? Bytes, string? ContentType, int HttpStatus, string? Error) DownloadFileBytes(
            string  fileId,
            int     timeoutMs,
            string? sharedLinkUrl = null)
        {
            string? lastError = null;

            for (int attempt = 1; attempt <= MaxRetries; attempt++)
            {
                var client = BoxSession.GetLoggedInClient(forceRelogin: lastError != null && IsAuthError(lastError));

                if (!string.IsNullOrWhiteSpace(sharedLinkUrl))
                {
                    _ = client.ExecuteAsync("GET", sharedLinkUrl!, null, null, timeoutMs)
                        .ConfigureAwait(false).GetAwaiter().GetResult();
                }

                var (bytes, contentType, httpStatus, error) = client
                    .DownloadFileAsync(fileId, timeoutMs)
                    .ConfigureAwait(false).GetAwaiter().GetResult();

                if (error == null)
                    return (bytes, contentType, httpStatus, null);

                lastError = error;
                bool netErr  = IsNetErr(error);
                bool authErr = IsAuthError(error);

                if ((netErr || authErr) && attempt < MaxRetries)
                {
                    if (netErr) Thread.Sleep(RetryBaseDelayMs * attempt);
                    continue;
                }

                break;
            }

            string hint = BuildDownloadErrorHint(lastError);
            return (null, null, 0, (lastError ?? "Unknown download error") + hint);
        }

        public static bool IsNetErr(string message)
        {
            if (string.IsNullOrEmpty(message)) return false;
            foreach (string kw in NetErrorKeywords)
                if (message.Contains(kw, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        private static readonly Regex NumericOnlyRegex =
            new(@"^\d+$", RegexOptions.Compiled);

        // Matches the token segment of a Box shared-link URL: https://<tenant>.box.com/s/<token>
        private static readonly Regex SharedLinkTokenInUrlRegex =
            new(@"/s/([A-Za-z0-9]{6,})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>True when <paramref name="s"/> is a plain Box numeric file ID (all digits).</summary>
        public static bool LooksLikeNumericFileId(string s) =>
            !string.IsNullOrWhiteSpace(s) && NumericOnlyRegex.IsMatch(s.Trim());

        public static string ExtractSharedToken(string raw)
        {
            raw = raw.Trim();

            Match m = SharedLinkTokenInUrlRegex.Match(raw);
            if (m.Success) return m.Groups[1].Value;

            int q = raw.IndexOf('?');
            if (q >= 0) raw = raw[..q];

            return raw.TrimEnd('/');
        }

        public static string GuessExtensionFromContentType(string contentType)
        {
            if (string.IsNullOrWhiteSpace(contentType))
                return ".bin";

            string ct = contentType.Split(';')[0].Trim().ToLowerInvariant();

            return ct switch
            {
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => ".xlsx",
                "application/vnd.openxmlformats-officedocument.presentationml.presentation" => ".pptx",
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => ".docx",
                "application/vnd.ms-excel" => ".xls",
                "application/vnd.ms-powerpoint" => ".ppt",
                "application/msword" => ".doc",
                "application/pdf" => ".pdf",
                "application/zip" or "application/x-zip-compressed" => ".zip",
                "text/csv" => ".csv",
                "text/plain" => ".txt",
                "image/png" => ".png",
                "image/jpeg" or "image/jpg" => ".jpg",
                "image/gif" => ".gif",
                "image/webp" => ".webp",
                "application/json" => ".json",
                "application/octet-stream" => ".bin",
                _ => ".bin",
            };
        }
    }
}
