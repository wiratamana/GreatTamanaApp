
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GTA.Tools.Impl.Common;

namespace GTA.Tools.Impl.IbmCloud;

/// <summary>One serializable cookie (domain/name/value/path) — used to persist a session between the two phases.</summary>
public sealed class CookieDto
{
    public string Domain { get; set; } = "";
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    public string Path { get; set; } = "/";
}

public sealed class OtpChallenge
{
    public string IamEndpoint { get; set; } = "";
    public string MfaState { get; set; } = "";
    public string MailMethodId { get; set; } = "";
    public string MailMethodName { get; set; } = "";
    public string Correlation { get; set; } = "";
    public string VerifyCsrfToken { get; set; } = "";
    public string MfaPageUrl { get; set; } = ""; // used as Referer on subsequent calls
    public List<CookieDto> Cookies { get; set; } = new();

    public bool SkippedMfa { get; set; } = false;

    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
    public static OtpChallenge FromJson(string json) => JsonSerializer.Deserialize<OtpChallenge>(json)!;
}

public sealed class LoggedInSession
{
    public List<CookieDto> Cookies { get; set; } = new();

    public string? IdentityToken => Cookies.FirstOrDefault(c => c.Name == "com.ibm.cloud.iam.Identity.prod")?.Value;
    public string? OpsConsoleSid => Cookies.FirstOrDefault(c => c.Name == "opsConsole.sid")?.Value;
    public long? ExpirationEpoch =>
        long.TryParse(Cookies.FirstOrDefault(c => c.Name == "com.ibm.cloud.iam.Expiration.prod")?.Value, out var v) ? v : null;

    public bool IsLocallyExpired => ExpirationEpoch is long exp && DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= exp;

    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
    public static LoggedInSession FromJson(string json) => JsonSerializer.Deserialize<LoggedInSession>(json)!;
}

/// <summary>Result of pinging IBM to confirm a cached session is still accepted server-side.</summary>
public sealed class SessionCheckResult
{
    public bool IsValid { get; set; }
    public string? IbmIdUser { get; set; }
    public string RawResponse { get; set; } = "";
}

public sealed class IbmCloudOtpLoginClient : IDisposable
{
    private readonly HttpClientHandler _handler;
    private readonly HttpClient _http;
    private readonly Action<string>? _log;
    private readonly IDiagnosticDumper? _dumper;

    public IbmCloudOtpLoginClient(Action<string>? logger = null, IDiagnosticDumper? dumper = null, bool useSystemProxyWithDefaultCredentials = true)
    {
        _log = logger;
        _dumper = dumper;
        _handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true,
            AllowAutoRedirect = false,
        };
        if (useSystemProxyWithDefaultCredentials)
        {
            _handler.UseProxy = true;
            _handler.DefaultProxyCredentials = CredentialCache.DefaultCredentials;
        }
        _http = new HttpClient(_handler);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Add("Accept-Language", "ja,en-US;q=0.9,en;q=0.8");
    }

    private void Log(string msg) => _log?.Invoke(msg);

    private static string? GetQueryParam(string url, string name)
    {
        var m = Regex.Match(url, $@"[?&]{Regex.Escape(name)}=([^&]*)");
        return m.Success ? Uri.UnescapeDataString(m.Groups[1].Value) : null;
    }

    private static List<CookieDto> Dump(CookieContainer cc) =>
        cc.GetAllCookies().Cast<Cookie>()
          .Select(c => new CookieDto { Domain = c.Domain, Name = c.Name, Value = c.Value, Path = c.Path })
          .ToList();

    private static void Restore(CookieContainer cc, IEnumerable<CookieDto> dtos)
    {
        foreach (var d in dtos)
        {
            try { cc.Add(new Cookie(d.Name, d.Value, d.Path, d.Domain)); }
            catch { /* ignore malformed/expired entries */ }
        }
    }

    private static readonly string[] TransientNetworkKeywords =
    {
        "強制的に切断",
        "接続はリモート",
        "forcibly closed",
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
        "proxy tunnel",
        "failed with status code '503'",
    };

    private static bool IsTransientNetworkException(Exception ex)
    {
        switch (ex)
        {
            case OperationCanceledException:
            case System.Net.Http.HttpRequestException:
            case System.Net.Sockets.SocketException:
                return true;
        }

        string? msg = ex.Message;
        if (!string.IsNullOrEmpty(msg))
            foreach (string kw in TransientNetworkKeywords)
                if (msg.Contains(kw, StringComparison.OrdinalIgnoreCase))
                    return true;

        return ex.InnerException != null && IsTransientNetworkException(ex.InnerException);
    }

    private async Task<HttpResponseMessage> SendWithTransientRetryAsync(Func<HttpRequestMessage> requestFactory, string label, int maxAttempts = 3, int baseDelayMs = 1500)
    {
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var req = requestFactory();
                return await _http.SendAsync(req);
            }
            catch (Exception ex) when (attempt < maxAttempts && IsTransientNetworkException(ex))
            {
                int delayMs = baseDelayMs * attempt;
                Log($"    [{label}] transient network error (attempt {attempt}/{maxAttempts}): {ex.Message} -- retrying in {delayMs}ms...");
                await Task.Delay(delayMs);
            }
        }
        // Final attempt: let it throw naturally if it fails again.
        using var finalReq = requestFactory();
        return await _http.SendAsync(finalReq);
    }

    /// <summary>Convenience overload of SendWithTransientRetryAsync for a plain GET.</summary>
    private Task<HttpResponseMessage> GetWithTransientRetryAsync(string url, string label, int maxAttempts = 3, int baseDelayMs = 1500) =>
        SendWithTransientRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, url), label, maxAttempts, baseDelayMs);

    public async Task<OtpChallenge> LoginAndRequestEmailOtpAsync(string email, string password)
    {
        void DumpDiagnostic(string name, string content) => _dumper?.Dump(name, content);
        Log("=== STEP 0: GET https://cloud.ibm.com/login ===");
        var resp0 = await _http.GetAsync("https://cloud.ibm.com/login");
        var body0 = await resp0.Content.ReadAsStringAsync();
        var csrfMatch = Regex.Match(body0, @"var csrfToken = '([^']+)';");
        if (!csrfMatch.Success) throw new InvalidOperationException("Could not find csrfToken on /login page.");
        string csrfToken = csrfMatch.Groups[1].Value;

        Log("=== STEP 1: POST /login/realm/IBMid (realm check) ===");
        var req1 = new HttpRequestMessage(HttpMethod.Post, "https://cloud.ibm.com/login/realm/IBMid?iamEndpoint=https%3A%2F%2Fiam.cloud.ibm.com");
        req1.Headers.Add("csrf-token", csrfToken);
        req1.Content = new StringContent(JsonSerializer.Serialize(new { username = email }), Encoding.UTF8, "application/json");
        var resp1 = await _http.SendAsync(req1);
        var body1 = await resp1.Content.ReadAsStringAsync();
        if (!resp1.IsSuccessStatusCode) throw new InvalidOperationException($"Realm check failed ({(int)resp1.StatusCode}).");
        using var doc1 = JsonDocument.Parse(body1);
        string clientId = doc1.RootElement.GetProperty("doFederated").GetProperty("clientId").GetString()!;
        string oidcCallbackUrl = doc1.RootElement.GetProperty("doFederated").GetProperty("oidcCallbackUrl").GetString()!;

        Log("=== STEP 2-4: federated login kick-off + OIDC hand-off redirects ===");
        string step2Url = "https://iam.cloud.ibm.com/identity/authorize" +
            $"?username={Uri.EscapeDataString(email)}&realm_id=IBMid&client_id={clientId}" +
            "&response_type=code&state=%252F%253FloginMethod%253Dfederated" +
            $"&redirect_uri={Uri.EscapeDataString(oidcCallbackUrl)}";

        static string ResolveLocation(HttpResponseMessage resp, string requestedUrl)
        {
            var locHeader = resp.Headers.Location;
            if (locHeader is null) return "";
            if (locHeader.IsAbsoluteUri) return locHeader.ToString();
            var baseUri = resp.RequestMessage?.RequestUri ?? new Uri(requestedUrl);
            return new Uri(baseUri, locHeader).ToString();
        }

        async Task<string> GetLocationOrThrowAsync(string url, string stepName, string dumpFileStem)
        {
            var resp = await _http.GetAsync(url);
            var body = await resp.Content.ReadAsStringAsync();
            DumpDiagnostic($"{dumpFileStem}_status{(int)resp.StatusCode}.html", body);
            var loc = ResolveLocation(resp, url);

            if (string.IsNullOrEmpty(loc))
            {
                var formActionMatch = Regex.Match(body, @"<form[^>]*\baction\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);
                var mcpcMatch = Regex.Match(body, @"name=[""']mcpc[""']\s+value=[""']([^""']+)[""']");
                if (!mcpcMatch.Success) mcpcMatch = Regex.Match(body, @"mcpc[""']?\s*[:=]\s*[""']([^""']+)[""']");
                if (formActionMatch.Success && mcpcMatch.Success)
                {
                    string formAction = System.Net.WebUtility.HtmlDecode(formActionMatch.Groups[1].Value);
                    Log($"    (no redirect; found an auto-post \"mcpc\" mirror form -> POSTing to {formAction})");
                    var mirrorReq = new HttpRequestMessage(HttpMethod.Post, formAction)
                    { Content = new StringContent($"mcpc={Uri.EscapeDataString(mcpcMatch.Groups[1].Value)}", Encoding.UTF8, "application/x-www-form-urlencoded") };
                    var mirrorResp = await _http.SendAsync(mirrorReq);
                    var mirrorBody = await mirrorResp.Content.ReadAsStringAsync();
                    DumpDiagnostic($"{dumpFileStem}_mirror_status{(int)mirrorResp.StatusCode}.html", mirrorBody);
                    loc = ResolveLocation(mirrorResp, formAction);
                    if (string.IsNullOrEmpty(loc)) body = mirrorBody; // for the error snippet below, if it still fails
                }
            }

            if (string.IsNullOrEmpty(loc))
            {
                string snippet = body[..Math.Min(500, body.Length)];
                throw new InvalidOperationException(
                    $"{stepName}: no redirect (status {(int)resp.StatusCode}). URL requested: {url}. " +
                    $"Body snippet ({body.Length} chars total): {snippet}");
            }
            return loc;
        }

        var loc2 = await GetLocationOrThrowAsync(step2Url, "Step 2", "step2_authorize");
        var loc3 = await GetLocationOrThrowAsync(loc2, "Step 3", "step3_hop1");
        var loc4 = await GetLocationOrThrowAsync(loc3, "Step 4", "step4_hop2");
        string sharedQuery = new Uri(loc4).Query.TrimStart('?');

        Log("=== STEP 5-7: password page ===");
        await _http.GetAsync(loc4); // /oidc/sps/auth
        await _http.GetAsync($"https://login.ibm.com/idaas/mtfim/sps/idaas/login?{sharedQuery}");
        var resp7 = await _http.GetAsync($"https://login.ibm.com/authsvc/mtfim/sps/authsvc?PolicyId=urn:ibm:security:authentication:asf:basicldapuser&{sharedQuery}");
        var body7 = await resp7.Content.ReadAsStringAsync();
        DumpDiagnostic($"step7_passwordpage_status{(int)resp7.StatusCode}.html", body7);
        var stateIdMatch = Regex.Match(body7, @"StateId=([a-zA-Z0-9\-]+)");
        if (!stateIdMatch.Success) throw new InvalidOperationException($"Could not find StateId on password page (status {(int)resp7.StatusCode}). Body snippet: {body7[..Math.Min(500, body7.Length)]}");
        string stateId = stateIdMatch.Groups[1].Value;

        Log("=== STEP 8: POST credentials ===");
        var formBody = $"operation=verify&login-form-type=pwd&redirectURL=&username={Uri.EscapeDataString(email)}&password={Uri.EscapeDataString(password)}";
        var req8 = new HttpRequestMessage(HttpMethod.Post, $"https://login.ibm.com/authsvc/mtfim/sps/authsvc?StateId={stateId}")
        { Content = new StringContent(formBody, Encoding.UTF8, "application/x-www-form-urlencoded") };
        var resp8 = await _http.SendAsync(req8);
        var loc8 = resp8.Headers.Location?.ToString();
        var body8 = await resp8.Content.ReadAsStringAsync();
        DumpDiagnostic($"step8_postcreds_status{(int)resp8.StatusCode}.html", body8);
        if (resp8.StatusCode != HttpStatusCode.Found || string.IsNullOrEmpty(loc8))
        {
            bool looksLikeLoginSuccess =
                body8.Contains("cs-login-success", StringComparison.OrdinalIgnoreCase) ||
                body8.Contains("LOGIN_SUCCESS", StringComparison.OrdinalIgnoreCase) ||
                body8.Contains("loginSuccess", StringComparison.OrdinalIgnoreCase);

            if (looksLikeLoginSuccess)
            {
                Log("    (status 200 \"login success\" shell seen -- credentials were ACCEPTED; " +
                    "attempting silent re-authorize now that a real session exists...)");

                var scriptSrcMatch = Regex.Match(body8, @"<script[^>]*\bsrc\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);
                if (scriptSrcMatch.Success)
                {
                    try
                    {
                        string scriptUrl = System.Net.WebUtility.HtmlDecode(scriptSrcMatch.Groups[1].Value);
                        string absoluteScriptUrl = scriptUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                            ? scriptUrl
                            : new Uri(resp8.RequestMessage?.RequestUri ?? new Uri("https://login.ibm.com/"), scriptUrl).ToString();
                        var scriptResp = await _http.GetAsync(absoluteScriptUrl);
                        var scriptBody = await scriptResp.Content.ReadAsStringAsync();
                        DumpDiagnostic("step8b_loginsuccess_script.js", scriptBody);
                    }
                    catch {  }
                }

                var respRetry = await _http.GetAsync(loc4);
                var bodyRetry = await respRetry.Content.ReadAsStringAsync();
                DumpDiagnostic($"step8c_reauth_status{(int)respRetry.StatusCode}.html", bodyRetry);

                bool landedOnRealAppPage =
                    respRetry.StatusCode == HttpStatusCode.OK &&
                    string.Equals(respRetry.RequestMessage?.RequestUri?.Host, "cloud.ibm.com", StringComparison.OrdinalIgnoreCase) &&
                    !bodyRetry.Contains("StateId=", StringComparison.OrdinalIgnoreCase) &&
                    !bodyRetry.Contains("cs-login-success", StringComparison.OrdinalIgnoreCase);

                if (landedOnRealAppPage)
                {
                    Log("    (silent re-authorize landed DIRECTLY on a real, authenticated cloud.ibm.com page -- " +
                        "MFA was skipped this run (likely a recent-MFA \"remembered device\" window). " +
                        "Returning an already-fully-authenticated result; no OTP needed this time.)");
                    return new OtpChallenge { SkippedMfa = true, Cookies = Dump(_handler.CookieContainer) };
                }

                loc8 = ResolveLocation(respRetry, loc4);
                if (string.IsNullOrEmpty(loc8)) body8 = bodyRetry; // for the error snippet below, if it still fails
            }

            if (string.IsNullOrEmpty(loc8))
            {
                throw new InvalidOperationException(
                    $"Credentials were rejected (status {(int)resp8.StatusCode}), or a \"login success\" shell was " +
                    $"seen but the silent-re-authorize retry still didn't yield a redirect. " +
                    $"See {(_dumper is null ? "(no IDiagnosticDumper injected)" : "the injected IDiagnosticDumper")} for full dumps. " +
                    $"Snippet: {body8[..Math.Min(500, body8.Length)]}");
            }
        }

        Log("=== STEP 9-11: OIDC code exchange -> MFA challenge page ===");
        var resp9 = await _http.GetAsync(loc8);
        var loc9 = resp9.Headers.Location?.ToString() ?? throw new InvalidOperationException("Step 9: no redirect.");
        var stateParam = GetQueryParam(loc9, "state");
        var resp10 = await _http.GetAsync(loc9);
        var loc10 = resp10.Headers.Location?.ToString() ?? throw new InvalidOperationException("Step 10: no redirect.");
        var resp11 = await _http.GetAsync(loc10);
        var body11 = await resp11.Content.ReadAsStringAsync();
        var mfaCsrfMatch = Regex.Match(body11, @"csrf\s*:\s*'([^']+)'");
        if (!mfaCsrfMatch.Success) throw new InvalidOperationException("Could not find MFA csrf token.");
        string mfaCsrf = mfaCsrfMatch.Groups[1].Value;
        var iamEndpointMatch = Regex.Match(body11, @"iam_endpoint\s*:\s*'([^']+)'");
        string iamEndpoint = iamEndpointMatch.Success ? iamEndpointMatch.Groups[1].Value : "https://identity-1.ap-north.iam.cloud.ibm.com";

        Log("=== STEP 12: query available MFA methods ===");
        var req12 = new HttpRequestMessage(HttpMethod.Post, $"{iamEndpoint}/uiaction/mfa_authenticate/authMethodsInfo")
        { Content = new StringContent($"csrf_token={mfaCsrf}", Encoding.UTF8, "application/x-www-form-urlencoded") };
        req12.Headers.Add("Accept", "application/json");
        req12.Headers.Add("X-Requested-With", "XMLHttpRequest");
        req12.Headers.Referrer = new Uri(loc10);
        var resp12 = await _http.SendAsync(req12);
        var body12 = await resp12.Content.ReadAsStringAsync();
        if (!resp12.IsSuccessStatusCode) throw new InvalidOperationException("authMethodsInfo call failed.");
        using var doc12 = JsonDocument.Parse(body12);
        if (!doc12.RootElement.TryGetProperty("mails", out var mailsEl) || mailsEl.GetArrayLength() == 0)
            throw new InvalidOperationException("This account has no email-OTP MFA method configured.");
        string mailMethodId = mailsEl[0].GetProperty("id").GetString()!;
        string mailMethodName = mailsEl[0].GetProperty("name").GetString() ?? "(unnamed)";
        string? csrfRefresh12 = resp12.Headers.TryGetValues("csrf-refresh", out var v12) ? v12.FirstOrDefault() : null;
        if (string.IsNullOrEmpty(csrfRefresh12)) throw new InvalidOperationException("No csrf-refresh header after authMethodsInfo.");
        string mfaState = stateParam ?? "";
        if (doc12.RootElement.TryGetProperty("initTxId", out var initTxIdEl)) mfaState = initTxIdEl.GetString() ?? mfaState;

        Log($"=== STEP 13: trigger email OTP send (method=\"{mailMethodName}\") ===");
        var req13 = new HttpRequestMessage(HttpMethod.Post, $"{iamEndpoint}/uiaction/mfa_authenticate/mfaauth_smsmailvoice_init?state={Uri.EscapeDataString(mfaState)}")
        { Content = new StringContent($"csrf_token={csrfRefresh12}&method_id={mailMethodId}", Encoding.UTF8, "application/x-www-form-urlencoded") };
        req13.Headers.Add("Accept", "application/json");
        req13.Headers.Add("X-Requested-With", "XMLHttpRequest");
        req13.Headers.Referrer = new Uri(loc10);
        var resp13 = await _http.SendAsync(req13);
        var body13 = await resp13.Content.ReadAsStringAsync();
        string? csrfRefresh13 = resp13.Headers.TryGetValues("csrf-refresh", out var v13) ? v13.FirstOrDefault() : null;
        if (!(resp13.IsSuccessStatusCode && body13.Contains("\"result\":\"ok\"")) || string.IsNullOrEmpty(csrfRefresh13))
            throw new InvalidOperationException($"OTP-send call did not succeed. Body: {body13}");
        using var doc13 = JsonDocument.Parse(body13);
        string correlation = doc13.RootElement.GetProperty("correlation").GetString() ?? "";

        Log($"=== Email OTP sent. correlation={correlation} ===");

        return new OtpChallenge
        {
            IamEndpoint = iamEndpoint,
            MfaState = mfaState,
            MailMethodId = mailMethodId,
            MailMethodName = mailMethodName,
            Correlation = correlation,
            VerifyCsrfToken = csrfRefresh13,
            MfaPageUrl = loc10,
            Cookies = Dump(_handler.CookieContainer),
        };
    }

    public async Task<LoggedInSession> SubmitOtpAsync(OtpChallenge challenge, string otpCode)
    {
        // accept "correlation-code" (e.g. "5945-231740") or just the bare code
        if (otpCode.Contains('-')) otpCode = otpCode.Split('-').Last();

        Restore(_handler.CookieContainer, challenge.Cookies);

        Log("=== STEP 14: submit OTP ===");
        var req14 = new HttpRequestMessage(HttpMethod.Post,
            $"{challenge.IamEndpoint}/uiaction/mfa_authenticate/mfaauth_smsmailvoice_verify?state={Uri.EscapeDataString(challenge.MfaState)}")
        {
            Content = new StringContent(
                $"csrf_token={challenge.VerifyCsrfToken}&method_id={challenge.MailMethodId}&correlation={challenge.Correlation}&security_code={Uri.EscapeDataString(otpCode)}",
                Encoding.UTF8, "application/x-www-form-urlencoded")
        };
        req14.Headers.Add("Accept", "application/json");
        req14.Headers.Add("X-Requested-With", "XMLHttpRequest");
        req14.Headers.Referrer = new Uri(challenge.MfaPageUrl);
        var resp14 = await _http.SendAsync(req14);
        var body14 = await resp14.Content.ReadAsStringAsync();
        string? csrfRefresh14 = resp14.Headers.TryGetValues("csrf-refresh", out var v14) ? v14.FirstOrDefault() : null;
        if (!resp14.IsSuccessStatusCode || string.IsNullOrEmpty(csrfRefresh14))
            throw new InvalidOperationException($"OTP was rejected (status {(int)resp14.StatusCode}). Body: {body14}");

        Log("=== STEP 15: continue -> IBM SSO mirror hand-off ===");
        string step15Url = $"{challenge.IamEndpoint}/uiaction/mfa_authenticate/continue?csrf_token={csrfRefresh14}&state={Uri.EscapeDataString(challenge.MfaState)}";
        var resp15 = await GetWithTransientRetryAsync(step15Url, "STEP 15 continue");
        var body15 = await resp15.Content.ReadAsStringAsync();

        string? nextUrl = resp15.Headers.Location?.ToString();
        if (string.IsNullOrEmpty(nextUrl))
        {
            var mcpcMatch = Regex.Match(body15, @"name=[""']mcpc[""']\s+value=[""']([^""']+)[""']");
            if (!mcpcMatch.Success) mcpcMatch = Regex.Match(body15, @"mcpc[""']?\s*[:=]\s*[""']([^""']+)[""']");
            if (mcpcMatch.Success)
            {
                string mcpcValue = mcpcMatch.Groups[1].Value;
                string mirrorUrl = $"https://iam.bluemix.net/identity/mirror?state={Uri.EscapeDataString(challenge.MfaState)}";
                var resp16 = await SendWithTransientRetryAsync(
                    () => new HttpRequestMessage(HttpMethod.Post, mirrorUrl)
                    { Content = new StringContent($"mcpc={mcpcValue}", Encoding.UTF8, "application/x-www-form-urlencoded") },
                    "STEP 16 mcpc-mirror");
                nextUrl = resp16.Headers.Location?.ToString();
            }
        }

        while (!string.IsNullOrEmpty(nextUrl))
        {
            var absolute = nextUrl.StartsWith("http") ? nextUrl : new Uri(new Uri("https://cloud.ibm.com/"), nextUrl).ToString();
            var respN = await GetWithTransientRetryAsync(absolute, "post-MFA redirect follow");
            nextUrl = respN.Headers.Location?.ToString();
        }

        Log("=== Login complete. Final session cookies captured. ===");
        return new LoggedInSession { Cookies = Dump(_handler.CookieContainer) };
    }

    public async Task<SessionCheckResult> IsSessionValidAsync(LoggedInSession session)
    {
        if (session.IsLocallyExpired)
            return new SessionCheckResult { IsValid = false, RawResponse = "(skipped network call — ExpirationEpoch already passed locally)" };

        Restore(_handler.CookieContainer, session.Cookies);

        var resp = await _http.GetAsync("https://login.ibm.com/v1/mgmt/idaas/user/status/");
        var body = await resp.Content.ReadAsStringAsync();

        string? user = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("user", out var userEl)) user = userEl.GetString();
        }
        catch { /* non-JSON body -> treated as invalid below */ }

        bool valid = !string.IsNullOrEmpty(user) && !string.Equals(user, "Unauthenticated", StringComparison.OrdinalIgnoreCase);
        return new SessionCheckResult { IsValid = valid, IbmIdUser = user, RawResponse = body };
    }

    public void Dispose() => _http.Dispose();
}
