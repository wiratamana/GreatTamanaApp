
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GTA.Tools.Impl.IbmCloud;

public sealed class CoralogixToken
{
    public string Token { get; set; } = "";
    /// <summary>Full cookie name it was minted as, e.g. "prod-jp-tok.coralogix_token".</summary>
    public string CookieName { get; set; } = "";
    public string Region { get; set; } = "";
    public int TeamId { get; set; } = 0;
    public DateTimeOffset AcquiredAtUtc { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Parsed from the Set-Cookie's Expires/Max-Age at mint time (Max-Age=31536000 = 1 year, as observed).</summary>
    public DateTimeOffset? ExpiresAtUtc { get; set; }

    public bool IsLocallyExpired => ExpiresAtUtc is DateTimeOffset exp && DateTimeOffset.UtcNow >= exp;

    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
    public static CoralogixToken FromJson(string json) => JsonSerializer.Deserialize<CoralogixToken>(json)!;
}

public sealed class CoralogixMfaChallenge
{
    public string IamEndpoint { get; set; } = "";
    public string MfaState { get; set; } = "";
    public string MailMethodId { get; set; } = "";
    public string MailMethodName { get; set; } = "";
    public string Correlation { get; set; } = "";
    public string VerifyCsrfToken { get; set; } = "";
    /// <summary>Used as the Referer header on the follow-up MFA calls, same as IbmCloudOtpLoginClient's MfaPageUrl.</summary>
    public string MfaPageUrl { get; set; } = "";
    public string CookieName { get; set; } = "";
    public string Region { get; set; } = "";
    public int TeamId { get; set; } = 0;
}

public sealed class CoralogixTokenResult
{
    public CoralogixToken? Token { get; private init; }
    public CoralogixMfaChallenge? Challenge { get; private init; }

    public bool RequiresMfa => Challenge is not null;

    public static CoralogixTokenResult ForToken(CoralogixToken token) => new() { Token = token };
    public static CoralogixTokenResult ForChallenge(CoralogixMfaChallenge challenge) => new() { Challenge = challenge };
}

public sealed class CoralogixLoginClient : IDisposable
{
    public string AccountId { get; set; } = GTA.Core.InitSettings.Get("ibm_account_id", "");
    public string ServiceInstanceId { get; set; } = GTA.Core.InitSettings.Get("ibm_jp_preprod_tenant_id", "");
    public int TeamId { get; set; } = ParseIniInt("ibm_jp_preprod_team_id");
    private static int ParseIniInt(string key) =>
        int.TryParse(GTA.Core.InitSettings.Get(key, ""), out int v) ? v : 0;
    public string Region { get; set; } = "jp-tok";
    public string IdentityAuthorizeHost { get; set; } = "identity-2.ap-north.iam.cloud.ibm.com";

    public const int MaxHandoffHops = 10;

    private readonly HttpClientHandler _handler;
    private readonly HttpClient _http;
    private readonly Action<string>? _log;

    public CoralogixLoginClient(Action<string>? logger = null, bool useSystemProxyWithDefaultCredentials = true)
    {
        _log = logger;
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

    private static void Restore(CookieContainer cc, IEnumerable<CookieDto> dtos)
    {
        foreach (var d in dtos)
        {
            try { cc.Add(new Cookie(d.Name, d.Value, d.Path, d.Domain)); }
            catch { /* ignore malformed/expired entries */ }
        }
    }

    private static string? ResolveLocationOrNull(HttpResponseMessage resp, string requestedUrl)
    {
        var locHeader = resp.Headers.Location;
        if (locHeader is null) return null;
        if (locHeader.IsAbsoluteUri) return locHeader.ToString();
        var baseUri = resp.RequestMessage?.RequestUri ?? new Uri(requestedUrl);
        return new Uri(baseUri, locHeader).ToString();
    }

    /// <summary>Pulls the "prod-{region}.coralogix_token" value out of a response's Set-Cookie header(s), if present.</summary>
    private static string? TryExtractCookieValue(HttpResponseMessage resp, string cookieName)
    {
        if (resp.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            foreach (var sc in setCookies)
            {
                var m = Regex.Match(sc, $@"{Regex.Escape(cookieName)}=([^;]+)");
                if (m.Success) return m.Groups[1].Value;
            }
        }
        return null;
    }

    /// <summary>Pulls the Expires= value off whichever Set-Cookie header actually carries cookieName, if any.</summary>
    private static DateTimeOffset? TryExtractCookieExpiry(HttpResponseMessage resp, string cookieName)
    {
        if (resp.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            foreach (var sc in setCookies)
            {
                if (!sc.Contains(cookieName)) continue;
                var expMatch = Regex.Match(sc, @"Expires=([^;]+)");
                if (expMatch.Success && DateTimeOffset.TryParse(expMatch.Groups[1].Value, out var exp))
                    return exp;
            }
        }
        return null;
    }

    private static (string Csrf, string IamEndpoint)? TryParseMfaChallengePage(string body)
    {
        if (!body.Contains("initMfaAuthenticate", StringComparison.OrdinalIgnoreCase)) return null;
        var csrfMatch = Regex.Match(body, @"csrf\s*:\s*'([^']+)'");
        var iamMatch = Regex.Match(body, @"iam_endpoint\s*:\s*'([^']+)'");
        if (!csrfMatch.Success || !iamMatch.Success) return null;
        return (csrfMatch.Groups[1].Value, iamMatch.Groups[1].Value);
    }

    public async Task<CoralogixTokenResult> GetTokenOrChallengeAsync(LoggedInSession ibmSession)
    {
        Restore(_handler.CookieContainer, ibmSession.Cookies);

        string crn = $"crn:v1:bluemix:public:logs:{Region}:a/{AccountId}:{ServiceInstanceId}::";
        string stateJson = JsonSerializer.Serialize(new
        {
            redirect_to = "/login",
            team_id = TeamId,
            service_instance = ServiceInstanceId,
            nonce = Guid.NewGuid().ToString(),
            crn,
        });
        string stateB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(stateJson));
        string redirectUri = $"https://login.api.{Region}.logs.cloud.ibm.com/oauth2/callback";
        string cookieName = $"prod-{Region}.coralogix_token";
        string currentUrl =
            $"https://{IdentityAuthorizeHost}/identity/authorize?client_id=logs" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&state={Uri.EscapeDataString(stateB64)}";

        Log("=== STEP A: GET /identity/authorize (IBM IAM -> Coralogix OAuth hand-off) ===");

        string? mfaStateFallback = null;

        for (int hop = 1; hop <= MaxHandoffHops; hop++)
        {
            var resp = await _http.GetAsync(currentUrl);

            string? tokenValue = TryExtractCookieValue(resp, cookieName);
            if (tokenValue is not null)
            {
                Log($"=== Coralogix token acquired (cookie \"{cookieName}\") at hop {hop}. ===");
                return CoralogixTokenResult.ForToken(new CoralogixToken
                {
                    Token = tokenValue,
                    CookieName = cookieName,
                    Region = Region,
                    TeamId = TeamId,
                    AcquiredAtUtc = DateTimeOffset.UtcNow,
                    ExpiresAtUtc = TryExtractCookieExpiry(resp, cookieName),
                });
            }

            string? loc = ResolveLocationOrNull(resp, currentUrl);
            if (!string.IsNullOrEmpty(loc))
            {
                var stateMatch = Regex.Match(loc, @"[?&]state=([^&]+)");
                if (stateMatch.Success) mfaStateFallback = Uri.UnescapeDataString(stateMatch.Groups[1].Value);

                Log($"    (hop {hop}: {(int)resp.StatusCode} -> {loc})");
                currentUrl = loc;
                continue;
            }

            // No redirect at all -- read the body to figure out what we actually landed on.
            string body = await resp.Content.ReadAsStringAsync();

            var mfaMatch = TryParseMfaChallengePage(body);
            if (mfaMatch is not null)
            {
                Log("    (no redirect; landed on IBM's step-up MFA challenge page -- triggering its email OTP send now)");
                var challenge = await TriggerMfaEmailOtpAsync(mfaMatch.Value.Csrf, mfaMatch.Value.IamEndpoint, currentUrl, mfaStateFallback, cookieName);
                return CoralogixTokenResult.ForChallenge(challenge);
            }

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

                string? mirrorToken = TryExtractCookieValue(mirrorResp, cookieName);
                if (mirrorToken is not null)
                {
                    Log($"=== Coralogix token acquired (cookie \"{cookieName}\") right after the mcpc-mirror hand-off. ===");
                    return CoralogixTokenResult.ForToken(new CoralogixToken
                    {
                        Token = mirrorToken,
                        CookieName = cookieName,
                        Region = Region,
                        TeamId = TeamId,
                        AcquiredAtUtc = DateTimeOffset.UtcNow,
                        ExpiresAtUtc = TryExtractCookieExpiry(mirrorResp, cookieName),
                    });
                }

                string? mirrorLoc = ResolveLocationOrNull(mirrorResp, formAction);
                if (!string.IsNullOrEmpty(mirrorLoc))
                {
                    currentUrl = mirrorLoc;
                    continue;
                }

                Log("    (mcpc-mirror hop had no redirect either -- retrying the original /identity/authorize URL)");
                currentUrl = $"https://{IdentityAuthorizeHost}/identity/authorize?client_id=logs" +
                    $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                    $"&state={Uri.EscapeDataString(stateB64)}";
                continue;
            }

            throw new InvalidOperationException(
                $"Coralogix OAuth hand-off dead-ended at hop {hop} (status {(int)resp.StatusCode}, no redirect, no coralogix_token cookie, " +
                $"and the body matched neither the step-up MFA page nor the mcpc-mirror pattern). URL: {currentUrl}. " +
                $"This usually means the supplied LoggedInSession is not actually logged in / has expired -- " +
                $"re-run IbmCloudAutoLoginClient.LoginAsync() first. Body snippet: {body[..Math.Min(400, body.Length)]}");
        }

        throw new InvalidOperationException(
            $"Coralogix OAuth hand-off did not complete within {MaxHandoffHops} redirect hops (last URL: {currentUrl}).");
    }

    private async Task<CoralogixMfaChallenge> TriggerMfaEmailOtpAsync(string csrf, string iamEndpoint, string mfaPageUrl, string? fallbackState, string cookieName)
    {
        Log("=== Querying available MFA methods for the Coralogix step-up challenge ===");
        var req12 = new HttpRequestMessage(HttpMethod.Post, $"{iamEndpoint}/uiaction/mfa_authenticate/authMethodsInfo")
        { Content = new StringContent($"csrf_token={csrf}", Encoding.UTF8, "application/x-www-form-urlencoded") };
        req12.Headers.Add("Accept", "application/json");
        req12.Headers.Add("X-Requested-With", "XMLHttpRequest");
        req12.Headers.Referrer = new Uri(mfaPageUrl);
        var resp12 = await _http.SendAsync(req12);
        var body12 = await resp12.Content.ReadAsStringAsync();
        if (!resp12.IsSuccessStatusCode)
            throw new InvalidOperationException($"Coralogix step-up MFA: authMethodsInfo call failed (status {(int)resp12.StatusCode}). Body: {body12}");

        using var doc12 = JsonDocument.Parse(body12);
        if (!doc12.RootElement.TryGetProperty("mails", out var mailsEl) || mailsEl.GetArrayLength() == 0)
            throw new InvalidOperationException("Coralogix step-up MFA: this account has no email-OTP MFA method configured.");
        string mailMethodId = mailsEl[0].GetProperty("id").GetString()!;
        string mailMethodName = mailsEl[0].GetProperty("name").GetString() ?? "(unnamed)";
        string? csrfRefresh12 = resp12.Headers.TryGetValues("csrf-refresh", out var v12) ? v12.FirstOrDefault() : null;
        if (string.IsNullOrEmpty(csrfRefresh12))
            throw new InvalidOperationException("Coralogix step-up MFA: no csrf-refresh header after authMethodsInfo.");
        string mfaState = fallbackState ?? "";
        if (doc12.RootElement.TryGetProperty("initTxId", out var initTxIdEl)) mfaState = initTxIdEl.GetString() ?? mfaState;

        Log($"=== Triggering email OTP send (method=\"{mailMethodName}\") for the Coralogix step-up challenge ===");
        var req13 = new HttpRequestMessage(HttpMethod.Post, $"{iamEndpoint}/uiaction/mfa_authenticate/mfaauth_smsmailvoice_init?state={Uri.EscapeDataString(mfaState)}")
        { Content = new StringContent($"csrf_token={csrfRefresh12}&method_id={mailMethodId}", Encoding.UTF8, "application/x-www-form-urlencoded") };
        req13.Headers.Add("Accept", "application/json");
        req13.Headers.Add("X-Requested-With", "XMLHttpRequest");
        req13.Headers.Referrer = new Uri(mfaPageUrl);
        var resp13 = await _http.SendAsync(req13);
        var body13 = await resp13.Content.ReadAsStringAsync();
        string? csrfRefresh13 = resp13.Headers.TryGetValues("csrf-refresh", out var v13) ? v13.FirstOrDefault() : null;
        if (!(resp13.IsSuccessStatusCode && body13.Contains("\"result\":\"ok\"")) || string.IsNullOrEmpty(csrfRefresh13))
            throw new InvalidOperationException($"Coralogix step-up MFA: OTP-send call did not succeed. Body: {body13}");
        using var doc13 = JsonDocument.Parse(body13);
        string correlation = doc13.RootElement.GetProperty("correlation").GetString() ?? "";

        Log($"=== Coralogix step-up email OTP sent. correlation={correlation} ===");

        return new CoralogixMfaChallenge
        {
            IamEndpoint = iamEndpoint,
            MfaState = mfaState,
            MailMethodId = mailMethodId,
            MailMethodName = mailMethodName,
            Correlation = correlation,
            VerifyCsrfToken = csrfRefresh13,
            MfaPageUrl = mfaPageUrl,
            CookieName = cookieName,
            Region = Region,
            TeamId = TeamId,
        };
    }

    public async Task<CoralogixToken> CompleteMfaAsync(CoralogixMfaChallenge challenge, string otpCode)
    {
        if (otpCode.Contains('-')) otpCode = otpCode.Split('-').Last();
        string cookieName = challenge.CookieName;

        Log("=== Submitting OTP for the Coralogix step-up MFA challenge ===");
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
            throw new InvalidOperationException($"Coralogix step-up MFA: OTP was rejected (status {(int)resp14.StatusCode}). Body: {body14}");

        Log("=== Continuing the Coralogix step-up hand-off after OTP verification ===");
        string continueUrl = $"{challenge.IamEndpoint}/uiaction/mfa_authenticate/continue?csrf_token={csrfRefresh14}&state={Uri.EscapeDataString(challenge.MfaState)}";
        var respContinue = await _http.GetAsync(continueUrl);

        string? tokenValue = TryExtractCookieValue(respContinue, cookieName);
        DateTimeOffset? expiresAt = TryExtractCookieExpiry(respContinue, cookieName);
        string? nextUrl = ResolveLocationOrNull(respContinue, continueUrl);

        if (tokenValue is null && string.IsNullOrEmpty(nextUrl))
        {
            // Same mcpc-mirror auto-post-back pattern as IbmCloudOtpLoginClient.SubmitOtpAsync's STEP 15/16.
            string bodyContinue = await respContinue.Content.ReadAsStringAsync();
            var mcpcMatch = Regex.Match(bodyContinue, @"name=[""']mcpc[""']\s+value=[""']([^""']+)[""']");
            if (!mcpcMatch.Success) mcpcMatch = Regex.Match(bodyContinue, @"mcpc[""']?\s*[:=]\s*[""']([^""']+)[""']");
            if (mcpcMatch.Success)
            {
                Log("    (no redirect after continue; found an mcpc-mirror hand-off -> POSTing it)");
                string mirrorUrl = $"https://iam.bluemix.net/identity/mirror?state={Uri.EscapeDataString(challenge.MfaState)}";
                var req16 = new HttpRequestMessage(HttpMethod.Post, mirrorUrl)
                { Content = new StringContent($"mcpc={mcpcMatch.Groups[1].Value}", Encoding.UTF8, "application/x-www-form-urlencoded") };
                var resp16 = await _http.SendAsync(req16);
                tokenValue = TryExtractCookieValue(resp16, cookieName);
                expiresAt ??= TryExtractCookieExpiry(resp16, cookieName);
                nextUrl = ResolveLocationOrNull(resp16, mirrorUrl);
            }
        }

        for (int hop = 0; hop < MaxHandoffHops && tokenValue is null && !string.IsNullOrEmpty(nextUrl); hop++)
        {
            string absolute = nextUrl!.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? nextUrl
                : new Uri(new Uri(challenge.IamEndpoint), nextUrl).ToString();
            Log($"    (following post-MFA redirect: {absolute})");
            var respN = await _http.GetAsync(absolute);
            tokenValue = TryExtractCookieValue(respN, cookieName);
            expiresAt ??= TryExtractCookieExpiry(respN, cookieName);
            nextUrl = ResolveLocationOrNull(respN, absolute);
        }

        // Fallback: HttpClientHandler's CookieContainer also absorbs Set-Cookie headers automatically -- check there too.
        if (tokenValue is null)
        {
            var cc = _handler.CookieContainer.GetCookies(new Uri("https://logs.cloud.ibm.com/"));
            tokenValue = cc[cookieName]?.Value;
        }

        if (tokenValue is null)
            throw new InvalidOperationException(
                $"Coralogix step-up MFA was completed (OTP accepted) but the hand-off still never minted a \"{cookieName}\" cookie after following all subsequent redirects.");

        Log($"=== Coralogix token acquired via step-up MFA (cookie \"{cookieName}\"). ===");
        return new CoralogixToken
        {
            Token = tokenValue,
            CookieName = cookieName,
            Region = challenge.Region,
            TeamId = challenge.TeamId,
            AcquiredAtUtc = DateTimeOffset.UtcNow,
            ExpiresAtUtc = expiresAt,
        };
    }

    public async Task<CoralogixToken> GetTokenAsync(LoggedInSession ibmSession)
    {
        var result = await GetTokenOrChallengeAsync(ibmSession);
        if (result.Token is not null) return result.Token;

        throw new InvalidOperationException(
            "Coralogix hand-off requires a step-up MFA (email OTP) challenge that GetTokenAsync cannot complete on its own " +
            "(no mailbox access here) -- call GetTokenOrChallengeAsync(session) instead, and if RequiresMfa is true, obtain " +
            "the OTP code out-of-band and call CompleteMfaAsync(challenge, code). See IbmCloudAutoLoginClient.GetCoralogixTokenAsync " +
            "for the complete, working orchestration (it already does exactly this via its OutlookMailboxClient).");
    }

    public void Dispose() => _http.Dispose();
}
