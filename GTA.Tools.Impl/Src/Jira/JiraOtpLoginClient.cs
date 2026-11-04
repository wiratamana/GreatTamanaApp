
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GTA.Tools.Impl.Common;

namespace GTA.Tools.Impl.Jira;

public sealed class JiraCookieDto
{
    public string Domain { get; set; } = "";
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    public string Path { get; set; } = "/";
}

public sealed class JiraLoginPreparation
{
    public string Email { get; set; } = "";
    public string Application { get; set; } = "";
    public string OrgId { get; set; } = "";
    public string ContinueForAuth { get; set; } = "";
    public string PlainContinueUrl { get; set; } = "";
    public string CsrfToken { get; set; } = "";
    public string AnonymousId { get; set; } = "";
    public List<JiraCookieDto> Cookies { get; set; } = new();

    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
    public static JiraLoginPreparation FromJson(string json) => JsonSerializer.Deserialize<JiraLoginPreparation>(json)!;
}

public sealed class JiraOtpChallenge
{
    public string Email { get; set; } = "";
    public string Application { get; set; } = "";
    public string OrgId { get; set; } = "";
    public string ContinueForAuth { get; set; } = "";
    public string PlainContinueUrl { get; set; } = "";
    public string CsrfToken { get; set; } = "";
    public string AnonymousId { get; set; } = "";
    /// <summary>The MfaChallenge JWT returned in the 412 body's "token" field.</summary>
    public string MfaChallengeToken { get; set; } = "";
    public bool SkippedMfa { get; set; } = false;
    public List<JiraCookieDto> Cookies { get; set; } = new();

    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
    public static JiraOtpChallenge FromJson(string json) => JsonSerializer.Deserialize<JiraOtpChallenge>(json)!;
}

public sealed class JiraSession
{
    public string SiteUrl { get; set; } = ""; // e.g. https://foo.atlassian.net
    public List<JiraCookieDto> Cookies { get; set; } = new();

    public string? TenantSessionToken => Cookies.FirstOrDefault(c => c.Name == "tenant.session.token")?.Value;
    public string? AtlassianXsrfToken => Cookies.FirstOrDefault(c => c.Name == "atlassian.xsrf.token")?.Value;
    public string? CloudSessionToken => Cookies.FirstOrDefault(c => c.Name == "cloud.session.token")?.Value;

    public DateTime ExpiresAtUtc => TenantSessionToken is { Length: > 0 } t ? JwtHelper.GetExpiryUtc(t) : DateTime.MinValue;
    public bool IsLocallyExpired => ExpiresAtUtc == DateTime.MinValue || DateTime.UtcNow >= ExpiresAtUtc;

    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
    public static JiraSession FromJson(string json) => JsonSerializer.Deserialize<JiraSession>(json)!;
}

/// <summary>Result of pinging Jira to confirm a cached session is still accepted server-side.</summary>
public sealed class JiraSessionCheckResult
{
    public bool IsValid { get; set; }
    public string? AccountId { get; set; }
    public string? DisplayName { get; set; }
    public string RawResponse { get; set; } = "";
}

internal static class JwtHelper
{
    public static DateTime GetExpiryUtc(string jwt)
    {
        try
        {
            string[] parts = jwt.Split('.');
            if (parts.Length < 2) return DateTime.MinValue;
            string payload = parts[1].Replace('-', '+').Replace('_', '/');
            switch (payload.Length % 4) { case 2: payload += "=="; break; case 3: payload += "="; break; }
            byte[] bytes = Convert.FromBase64String(payload);
            using var doc = JsonDocument.Parse(bytes);
            long exp = doc.RootElement.GetProperty("exp").GetInt64();
            return DateTimeOffset.FromUnixTimeSeconds(exp).UtcDateTime;
        }
        catch { return DateTime.MinValue; }
    }
}

public sealed class JiraOtpLoginClient : IDisposable
{
    public const string RecaptchaSiteKey = "6Le9VxMnAAAAALKRlj8jmKwUYmFTZWcF0y4o9QeD";

    private readonly HttpClientHandler _handler;
    private readonly HttpClient _http;
    private readonly Action<string>? _log;
    private readonly IDiagnosticDumper? _dumper;

    public Func<string, Task<string>> RecaptchaTokenProvider { get; set; } = _ => Task.FromResult("");

    public JiraOtpLoginClient(
        Action<string>? logger = null,
        IDiagnosticDumper? dumper = null,
        Func<string, Task<string>>? recaptchaTokenProvider = null,
        bool useSystemProxyWithDefaultCredentials = true)
    {
        _log = logger;
        _dumper = dumper;
        if (recaptchaTokenProvider is not null) RecaptchaTokenProvider = recaptchaTokenProvider;

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
        _http.DefaultRequestHeaders.Add("Accept-Language", "ja");
    }

    private void Log(string msg) => _log?.Invoke(msg);
    private void Dump(string name, string content) => _dumper?.Dump(name, content);

    private static string? GetQueryParam(string url, string name)
    {
        var m = Regex.Match(url, $@"[?&]{Regex.Escape(name)}=([^&]*)");
        return m.Success ? Uri.UnescapeDataString(m.Groups[1].Value) : null;
    }

    private static string ResolveLocation(HttpResponseMessage resp, string requestedUrl)
    {
        var locHeader = resp.Headers.Location;
        if (locHeader is null) return "";
        if (locHeader.IsAbsoluteUri) return locHeader.ToString();
        var baseUri = resp.RequestMessage?.RequestUri ?? new Uri(requestedUrl);
        return new Uri(baseUri, locHeader).ToString();
    }

    private static List<JiraCookieDto> DumpCookies(CookieContainer cc) =>
        cc.GetAllCookies().Cast<Cookie>()
          .Select(c => new JiraCookieDto { Domain = c.Domain, Name = c.Name, Value = c.Value, Path = c.Path })
          .ToList();

    private static void RestoreCookies(CookieContainer cc, IEnumerable<JiraCookieDto> dtos)
    {
        foreach (var d in dtos)
        {
            try { cc.Add(new Cookie(d.Name, d.Value, d.Path, d.Domain)); }
            catch { /* ignore malformed/expired entries */ }
        }
    }

    #region Trial 1 -- prepare (no credentials sent)

    public async Task<JiraLoginPreparation> LoginAndPrepareAsync(string jiraSiteUrl, string protectedPath, string email)
    {
        jiraSiteUrl = jiraSiteUrl.TrimEnd('/');
        if (!protectedPath.StartsWith('/')) protectedPath = "/" + protectedPath;

        string loginUrl = $"{jiraSiteUrl}/login?application=jira&dest-url={Uri.EscapeDataString(protectedPath)}&permission-violation=true";

        Log($"=== STEP 1: GET {loginUrl} (expect redirect to id.atlassian.com/login) ===");
        var resp1 = await _http.GetAsync(loginUrl);
        string loc1 = ResolveLocation(resp1, loginUrl);
        if (string.IsNullOrEmpty(loc1))
        {
            string body1 = await resp1.Content.ReadAsStringAsync();
            Dump("step1_no_redirect.html", body1);
            throw new InvalidOperationException(
                $"Step 1: expected a redirect to id.atlassian.com/login but got status {(int)resp1.StatusCode} " +
                $"with no Location header (already logged in? session cookies leaking in from a previous run?). " +
                $"Body snippet: {body1[..Math.Min(300, body1.Length)]}");
        }
        Log($"    -> redirected to: {loc1}");

        string? application = GetQueryParam(loc1, "application");
        string? orgId = GetQueryParam(loc1, "orgId");
        string? continueForAuth = GetQueryParam(loc1, "continue");
        if (string.IsNullOrEmpty(continueForAuth))
            throw new InvalidOperationException($"Step 1: could not find a \"continue\" query param on the login redirect: {loc1}");

        string? plainContinueUrl = GetQueryParam(continueForAuth, "continue") ?? (jiraSiteUrl + protectedPath);

        Log("=== STEP 2: GET the id.atlassian.com/login page (SPA shell -- loaded for realism only) ===");
        var resp2 = await _http.GetAsync(loc1);
        string body2 = await resp2.Content.ReadAsStringAsync();
        Dump($"step2_loginpage_status{(int)resp2.StatusCode}.html", body2);

        Log("=== STEP 2b: GET /frontend/state (mints atlassian.account.xsrf.token) ===");
        var stateResp = await _http.GetAsync("https://id.atlassian.com/frontend/state");
        string stateBody = await stateResp.Content.ReadAsStringAsync();
        Dump($"step2b_frontendstate_status{(int)stateResp.StatusCode}.json", stateBody);

        string? csrfToken = null;
        try
        {
            using var stateDoc = JsonDocument.Parse(stateBody);
            if (stateDoc.RootElement.TryGetProperty("csrfToken", out var t)) csrfToken = t.GetString();
        }
        catch { /* fall through to the cookie-based fallback below */ }
        csrfToken ??= GetCookieValue("atlassian.account.xsrf.token");

        if (string.IsNullOrEmpty(csrfToken))
            throw new InvalidOperationException(
                $"Step 2b: could not obtain a csrfToken from /frontend/state (status {(int)stateResp.StatusCode}). " +
                "Cannot proceed -- this value is reused as state.csrfToken in step 4 and as the x-csrf-token header in step 6. " +
                $"Body snippet: {stateBody[..Math.Min(300, stateBody.Length)]}");

        string anonymousId = Guid.NewGuid().ToString();
        string cuid = Guid.NewGuid().ToString("N");
        try
        {
            _handler.CookieContainer.Add(new Cookie("ajs_anonymous_id", Uri.EscapeDataString($"\"{anonymousId}\""), "/", "id.atlassian.com"));
            _handler.CookieContainer.Add(new Cookie("__cuid", cuid, "/", "id.atlassian.com"));
        }
        catch {  }

        Log($"    csrfToken={csrfToken}  anonymousId={anonymousId} (locally generated)  cuid={cuid} (locally generated)");
        Log($"    application={application}  orgId={orgId}");
        Log($"    continueForAuth={continueForAuth}");
        Log($"    plainContinueUrl={plainContinueUrl}");

        return new JiraLoginPreparation
        {
            Email = email,
            Application = application ?? "jira",
            OrgId = orgId ?? "",
            ContinueForAuth = continueForAuth,
            PlainContinueUrl = plainContinueUrl,
            CsrfToken = csrfToken,
            AnonymousId = anonymousId,
            Cookies = DumpCookies(_handler.CookieContainer),
        };
    }

    private string? GetCookieValue(string name)
    {
        // CookieContainer has no direct "get by name across all domains" API -- GetAllCookies() is the only enumeration path.
        return _handler.CookieContainer.GetAllCookies().Cast<Cookie>()
            .FirstOrDefault(c => c.Name == name)?.Value;
    }

    #endregion

    #region Trial 2/3 -- submit password

    public async Task<JiraOtpChallenge> SubmitPasswordAsync(JiraLoginPreparation prep, string password, string? recaptchaToken = null)
    {
        RestoreCookies(_handler.CookieContainer, prep.Cookies);
        recaptchaToken ??= await RecaptchaTokenProvider("authenticate");

        string url = $"https://id.atlassian.com/rest/authenticate?application={Uri.EscapeDataString(prep.Application)}" +
                     $"&continue={Uri.EscapeDataString(prep.ContinueForAuth)}" +
                     $"&email={Uri.EscapeDataString(prep.Email)}" +
                     $"&orgId={Uri.EscapeDataString(prep.OrgId)}";

        var bodyObj = new
        {
            username = prep.Email,
            password,
            state = new { csrfToken = prep.CsrfToken, anonymousId = prep.AnonymousId },
            token = recaptchaToken,
        };

        Log("=== STEP 4: POST /rest/authenticate (the real password submit) ===");
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(bodyObj), Encoding.UTF8, "application/json"),
        };
        req.Headers.Referrer = new Uri($"https://id.atlassian.com/login?continue={Uri.EscapeDataString(prep.ContinueForAuth)}&application={prep.Application}&orgId={prep.OrgId}&email={Uri.EscapeDataString(prep.Email)}");
        req.Headers.Add("Origin", "https://id.atlassian.com");

        var resp = await _http.SendAsync(req);
        string body = await resp.Content.ReadAsStringAsync();
        Dump($"step4_authenticate_status{(int)resp.StatusCode}.json", body);

        if (resp.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            // Expected path: MFA required.
            string? mfaToken = null;
            string? errorCode = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("token", out var t)) mfaToken = t.GetString();
                if (doc.RootElement.TryGetProperty("error", out var e)) errorCode = e.GetString();
            }
            catch { /* fall through to the error below */ }

            if (!string.Equals(errorCode, "mfa_required", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(mfaToken))
                throw new InvalidOperationException($"Step 4: got 412 but it wasn't the expected \"mfa_required\" shape. Body: {body}");

            Log("    -> 412 mfa_required (expected) -- MfaChallenge token captured.");
            return new JiraOtpChallenge
            {
                Email = prep.Email,
                Application = prep.Application,
                OrgId = prep.OrgId,
                ContinueForAuth = prep.ContinueForAuth,
                PlainContinueUrl = prep.PlainContinueUrl,
                CsrfToken = prep.CsrfToken,
                AnonymousId = prep.AnonymousId,
                MfaChallengeToken = mfaToken,
                SkippedMfa = false,
                Cookies = DumpCookies(_handler.CookieContainer),
            };
        }

        if (resp.IsSuccessStatusCode)
        {
            Log("    -> non-412 success status; treating as \"MFA skipped\" (unexpected but handled).");
            return new JiraOtpChallenge
            {
                Email = prep.Email,
                Application = prep.Application,
                OrgId = prep.OrgId,
                ContinueForAuth = prep.ContinueForAuth,
                PlainContinueUrl = prep.PlainContinueUrl,
                CsrfToken = prep.CsrfToken,
                AnonymousId = prep.AnonymousId,
                SkippedMfa = true,
                Cookies = DumpCookies(_handler.CookieContainer),
            };
        }

        // Hard failure -- e.g. wrong password, or a hard reCAPTCHA-token rejection (section 4a's other outcome).
        throw new InvalidOperationException(
            $"Step 4: /rest/authenticate failed with status {(int)resp.StatusCode} (not the expected 412 mfa_required). " +
            $"This is exactly the signal the strategy doc's section 4a is looking for -- check whether the body below " +
            $"mentions \"token\"/\"captcha\"/\"invalid_request\" (hard reCAPTCHA requirement) vs. a plain credential " +
            $"rejection. Body: {body}");
    }

    #endregion

    #region OTP submit + full post-MFA session hand-off

    public async Task<JiraSession> SubmitOtpAsync(JiraOtpChallenge challenge, string otpCode, string? recaptchaToken = null)
    {
        if (challenge.SkippedMfa)
            throw new InvalidOperationException("This challenge already skipped MFA (SkippedMfa=true) -- there is no OTP to submit; build the JiraSession directly from challenge.Cookies instead.");

        RestoreCookies(_handler.CookieContainer, challenge.Cookies);
        recaptchaToken ??= await RecaptchaTokenProvider("mfa_verify");

        string commonQuery = $"application={Uri.EscapeDataString(challenge.Application)}" +
                              $"&continue={Uri.EscapeDataString(challenge.ContinueForAuth)}" +
                              $"&email={Uri.EscapeDataString(challenge.Email)}" +
                              $"&orgId={Uri.EscapeDataString(challenge.OrgId)}";

        Log("=== STEP 6: POST /rest/mfa/verify ===");
        string verifyUrl = $"https://id.atlassian.com/rest/mfa/verify?{commonQuery}&transactionToken={Uri.EscapeDataString(challenge.MfaChallengeToken)}";
        var verifyReq = new HttpRequestMessage(HttpMethod.Post, verifyUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { otpCode, token = recaptchaToken }), Encoding.UTF8, "application/json"),
        };
        verifyReq.Headers.Add("x-csrf-token", challenge.CsrfToken);
        verifyReq.Headers.Add("Origin", "https://id.atlassian.com");
        var verifyResp = await _http.SendAsync(verifyReq);
        string verifyBody = await verifyResp.Content.ReadAsStringAsync();
        Dump($"step6_mfaverify_status{(int)verifyResp.StatusCode}.json", verifyBody);
        if (!verifyResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Step 6: /rest/mfa/verify failed (status {(int)verifyResp.StatusCode}). Body: {verifyBody}");

        string mfaAuthorizeToken;
        using (var doc = JsonDocument.Parse(verifyBody))
            mfaAuthorizeToken = doc.RootElement.GetProperty("transactionToken").GetString()
                ?? throw new InvalidOperationException($"Step 6: response had no transactionToken. Body: {verifyBody}");
        Log("    -> OTP accepted. MfaAuthorize token captured.");

        Log("=== STEP 7: GET /rest/mfa/authorize ===");
        string authorizeUrl = $"https://id.atlassian.com/rest/mfa/authorize?{commonQuery}&transactionToken={Uri.EscapeDataString(mfaAuthorizeToken)}";
        var authorizeResp = await _http.GetAsync(authorizeUrl);
        string authorizeBody = await authorizeResp.Content.ReadAsStringAsync();
        Dump($"step7_mfaauthorize_status{(int)authorizeResp.StatusCode}.json", authorizeBody);
        if (!authorizeResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Step 7: /rest/mfa/authorize failed (status {(int)authorizeResp.StatusCode}). Body: {authorizeBody}");

        string redirectUri;
        using (var doc = JsonDocument.Parse(authorizeBody))
            redirectUri = doc.RootElement.GetProperty("redirectUri").GetString()
                ?? throw new InvalidOperationException($"Step 7: response had no redirectUri. Body: {authorizeBody}");

        Log("=== STEP 8: GET redirectUri (mints cloud.session.token, the ACCOUNT-wide Atlassian ID session) ===");
        var step8Resp = await _http.GetAsync(redirectUri);
        _ = await step8Resp.Content.ReadAsStringAsync(); // SPA shell HTML; not needed, only its Set-Cookie matters
        if (GetCookieValue("cloud.session.token") is null)
            throw new InvalidOperationException($"Step 8: cloud.session.token was not set after GET {redirectUri} (status {(int)step8Resp.StatusCode}).");
        Log("    -> cloud.session.token acquired.");

        Log("=== STEP 9: GET /rest/login/state (asks for a fresh SITE-bound login token) ===");
        string loginStateUrl = $"https://id.atlassian.com/rest/login/state?continue={Uri.EscapeDataString(challenge.PlainContinueUrl)}" +
                                $"&login_hint={Uri.EscapeDataString(challenge.Email)}&product=jira";
        var loginStateResp = await _http.GetAsync(loginStateUrl);
        string loginStateBody = await loginStateResp.Content.ReadAsStringAsync();
        Dump($"step9_loginstate_status{(int)loginStateResp.StatusCode}.json", loginStateBody);
        if (!loginStateResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Step 9: /rest/login/state failed (status {(int)loginStateResp.StatusCode}). Body: {loginStateBody}");

        string sessionRefreshRedirect;
        using (var doc = JsonDocument.Parse(loginStateBody))
            sessionRefreshRedirect = doc.RootElement.GetProperty("redirectTo").GetString()
                ?? throw new InvalidOperationException($"Step 9: response had no redirectTo. Body: {loginStateBody}");

        Log("=== STEP 10: GET that redirectTo (expect 303 -> <site>/SetCST/initiate) ===");
        var step10Resp = await _http.GetAsync(sessionRefreshRedirect);
        string setCstInitiateUrl = ResolveLocation(step10Resp, sessionRefreshRedirect);
        if (string.IsNullOrEmpty(setCstInitiateUrl))
        {
            string b = await step10Resp.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Step 10: expected a redirect (status {(int)step10Resp.StatusCode}), got none. Body snippet: {b[..Math.Min(300, b.Length)]}");
        }
        Log($"    -> {setCstInitiateUrl}");

        Log("=== STEP 11: GET <site>/SetCST/initiate (expect 302 -> id.atlassian.com/login/authorize?...&state=...) ===");
        var step11Resp = await _http.GetAsync(setCstInitiateUrl);
        string stateAuthorizeUrl = ResolveLocation(step11Resp, setCstInitiateUrl);
        if (string.IsNullOrEmpty(stateAuthorizeUrl))
        {
            string b = await step11Resp.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Step 11: expected a redirect (status {(int)step11Resp.StatusCode}), got none. Body snippet: {b[..Math.Min(300, b.Length)]}");
        }
        Log($"    -> {stateAuthorizeUrl}");

        Log("=== STEP 12: GET that URL (200 HTML with a <meta http-equiv=\"refresh\"> pointing at <site>/SetCST?cst=...) ===");
        var step12Resp = await _http.GetAsync(stateAuthorizeUrl);
        string step12Body = await step12Resp.Content.ReadAsStringAsync();
        Dump($"step12_metarefresh_status{(int)step12Resp.StatusCode}.html", step12Body);
        var metaMatch = Regex.Match(step12Body, @"<meta\s+http-equiv=[""']refresh[""']\s+content=[""']0;\s*url=([^""']+)[""']", RegexOptions.IgnoreCase);
        if (!metaMatch.Success)
            throw new InvalidOperationException($"Step 12: could not find the expected <meta refresh> tag (status {(int)step12Resp.StatusCode}). Body snippet: {step12Body[..Math.Min(500, step12Body.Length)]}");
        string setCstUrl = WebUtility.HtmlDecode(metaMatch.Groups[1].Value);
        Log($"    -> parsed SetCST URL: {setCstUrl[..Math.Min(120, setCstUrl.Length)]}...");

        Log("=== STEP 13: GET <site>/SetCST?cst=... (THIS mints tenant.session.token) ===");
        var step13Resp = await _http.GetAsync(setCstUrl);
        string finalLandingUrl = ResolveLocation(step13Resp, setCstUrl);
        if (GetCookieValue("tenant.session.token") is null)
        {
            string b = await step13Resp.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"Step 13: tenant.session.token was not set after GET SetCST (status {(int)step13Resp.StatusCode}). " +
                $"Body snippet: {b[..Math.Min(300, b.Length)]}");
        }
        Log("    -> tenant.session.token acquired!");

        if (!string.IsNullOrEmpty(finalLandingUrl))
        {
            Log($"=== STEP 14 (confirmation): GET {finalLandingUrl} ===");
            var finalResp = await _http.GetAsync(finalLandingUrl);
            Log($"    -> status {(int)finalResp.StatusCode}");
        }

        string siteUrl = new Uri(challenge.PlainContinueUrl).GetLeftPart(UriPartial.Authority);
        Log("=== Login complete. Final session cookies captured. ===");
        return new JiraSession { SiteUrl = siteUrl, Cookies = DumpCookies(_handler.CookieContainer) };
    }

    #endregion

    #region Session reuse / heartbeat

    public async Task<JiraSessionCheckResult> IsSessionValidAsync(JiraSession session)
    {
        if (session.IsLocallyExpired)
            return new JiraSessionCheckResult { IsValid = false, RawResponse = "(skipped network call -- ExpiresAtUtc already passed locally)" };

        RestoreCookies(_handler.CookieContainer, session.Cookies);

        var resp = await _http.GetAsync($"{session.SiteUrl.TrimEnd('/')}/rest/api/2/myself");
        string body = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode)
            return new JiraSessionCheckResult { IsValid = false, RawResponse = body };

        string? accountId = null, displayName = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("accountId", out var a)) accountId = a.GetString();
            if (doc.RootElement.TryGetProperty("displayName", out var d)) displayName = d.GetString();
        }
        catch { /* non-JSON body -> treated as invalid below */ }

        return new JiraSessionCheckResult { IsValid = accountId is not null, AccountId = accountId, DisplayName = displayName, RawResponse = body };
    }

    public async Task<JiraSession> SendHeartbeatAsync(JiraSession session)
    {
        RestoreCookies(_handler.CookieContainer, session.Cookies);
        var req = new HttpRequestMessage(HttpMethod.Post, $"{session.SiteUrl.TrimEnd('/')}/gateway/api/session/heartbeat")
        {
            Content = new StringContent("", Encoding.UTF8, "application/json"),
        };
        var resp = await _http.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Heartbeat failed (status {(int)resp.StatusCode}).");
        return new JiraSession { SiteUrl = session.SiteUrl, Cookies = DumpCookies(_handler.CookieContainer) };
    }

    #endregion

    public void Dispose() => _http.Dispose();
}
