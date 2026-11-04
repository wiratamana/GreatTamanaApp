
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using GTA.Core;

namespace GTA.Tools.Impl.Jira
{
    public static class JiraDirectHttpClient
    {
        /// <summary>Credentials\JiraApiToken.json (via CredentialManager.Load).</summary>
        private const string CredentialFileName = "JiraApiToken";

        private static readonly object _clientLock = new();
        private static HttpClient? _client;
        private static DateTime _credFileStampUtc = DateTime.MinValue;

        private static string CredentialFilePath =>
            Path.Combine(CredentialManager.CredentialsFolder, CredentialFileName + ".json");

        private static HttpClient GetClient()
        {
            lock (_clientLock)
            {
                string path = CredentialFilePath;
                DateTime stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;

                if (_client != null && stamp == _credFileStampUtc)
                    return _client;

                HttpClient fresh = CreateClient();
                _client = fresh;
                _credFileStampUtc = stamp;
                return fresh;
            }
        }

        private static HttpClient CreateClient()
        {
            AccountCredential cred = CredentialManager.Load(CredentialFileName);

            var handler = new HttpClientHandler
            {
                UseProxy                = true,
                DefaultProxyCredentials = CredentialCache.DefaultCredentials,
                AllowAutoRedirect       = true,
            };

            var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{cred.Id}:{cred.Password}")));
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            return http;
        }

        public static (int StatusCode, string? Body, string? TransportError) ExecuteRestApiCall(
            string url, string httpMethod, string? body,
            Dictionary<string, string>? headers, int timeoutMs)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(Math.Max(1_000, timeoutMs)));

                string method = string.IsNullOrWhiteSpace(httpMethod) ? "GET" : httpMethod.Trim().ToUpperInvariant();
                using var req = new HttpRequestMessage(new HttpMethod(method), url);

                string? contentTypeOverride = null;
                if (headers != null)
                    foreach (var kv in headers)
                        if (string.Equals(kv.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                            contentTypeOverride = kv.Value;

                if (!string.IsNullOrEmpty(body))
                    req.Content = new StringContent(body, Encoding.UTF8, contentTypeOverride ?? "application/json");

                if (headers != null)
                    foreach (var kv in headers)
                    {
                        if (string.Equals(kv.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                            continue; // already applied to Content above
                        if (!req.Headers.TryAddWithoutValidation(kv.Key, kv.Value))
                            req.Content?.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                    }

                using HttpResponseMessage resp = GetClient().Send(req, cts.Token);
                string respBody = new StreamReader(resp.Content.ReadAsStream(cts.Token)).ReadToEnd();

                return ((int)resp.StatusCode, respBody, null);
            }
            catch (OperationCanceledException)
            {
                return (0, null, $"Request to '{url}' timed out after {timeoutMs} ms.");
            }
            catch (Exception ex)
            {
                return (0, null, ex.Message);
            }
        }

        public static (byte[]? Bytes, string? ContentType, string? TransportError) DownloadBytes(
            string url, int timeoutMs)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(Math.Max(1_000, timeoutMs)));

                using HttpResponseMessage resp = GetClient()
                    .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                    .GetAwaiter().GetResult();

                if (!resp.IsSuccessStatusCode)
                {
                    string errBody = resp.Content.ReadAsStringAsync(cts.Token).GetAwaiter().GetResult();
                    return (null, null, $"HTTP {(int)resp.StatusCode} downloading '{url}'" +
                        (string.IsNullOrWhiteSpace(errBody) ? "" : $": {errBody}"));
                }

                byte[] bytes = resp.Content.ReadAsByteArrayAsync(cts.Token).GetAwaiter().GetResult();
                string? contentType = resp.Content.Headers.ContentType?.MediaType;
                return (bytes, contentType, null);
            }
            catch (OperationCanceledException)
            {
                return (null, null, $"Download from '{url}' timed out after {timeoutMs} ms.");
            }
            catch (Exception ex)
            {
                return (null, null, ex.Message);
            }
        }
    }
}
