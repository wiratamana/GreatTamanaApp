
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GTA.Tools.Impl.Common;

namespace GTA.Tools.Impl.Outlook;

public sealed class OutlookOAuthTokens
{
    public string AccessToken { get; set; } = "";
    public string? RefreshToken { get; set; }
    public string? IdToken { get; set; }
    public int ExpiresIn { get; set; }
    public string RawTokenResponse { get; set; } = "";
}

public sealed class OutlookAdfsLoginClient : IDisposable
{
    private const string ClientId = "9199bf20-a13f-4107-85dc-02114787ef48";
    private const string RedirectUri = "https://outlook.live.com/mail/";
    private const string Scope = "https://outlook.office.com/.default openid profile offline_access";

    private readonly HttpClientHandler _handler;
    private readonly HttpClient _http;
    private readonly Action<string>? _log;
    private readonly IDiagnosticDumper? _dumper;

    public OutlookAdfsLoginClient(Action<string>? logger = null, IDiagnosticDumper? dumper = null, bool useSystemProxyWithDefaultCredentials = true)
    {
        _log = logger;
        _dumper = dumper;
        _handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true,
            AllowAutoRedirect = false,
        };

        _handler.CookieContainer.Add(new Cookie("SSOCOOKIEPULLED", "1", "/", "login.microsoftonline.com"));
        _handler.CookieContainer.Add(new Cookie("AADSSO", "NA|NoExtension", "/", "login.microsoftonline.com"));

        if (useSystemProxyWithDefaultCredentials)
        {
            _handler.UseProxy = true;
            _handler.DefaultProxyCredentials = CredentialCache.DefaultCredentials;
        }
        _http = new HttpClient(_handler) { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Add("Accept-Language", "ja,en-US;q=0.9,en;q=0.8");
    }

    private void Log(string msg) => _log?.Invoke(msg);

    private void LogCookieJar(string label)
    {
        var cookies = _handler.CookieContainer.GetCookies(new Uri("https://login.microsoftonline.com/")).Cast<Cookie>().ToList();
        Log($"    [cookie jar @ {label}] {cookies.Count} cookie(s) for login.microsoftonline.com:");
        foreach (var c in cookies.OrderBy(c => c.Name))
            Log($"        {c.Name} = ({c.Value.Length} chars) {(c.Value.Length > 0 ? c.Value[..Math.Min(12, c.Value.Length)] : "")}...  [HttpOnly={c.HttpOnly}, Secure={c.Secure}, Expires={(c.Expires == default ? "session" : c.Expires.ToString("u"))}]");
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static (string verifier, string challenge) GeneratePkce()
    {
        var verifierBytes = RandomNumberGenerator.GetBytes(32);
        string verifier = Base64UrlEncode(verifierBytes);
        byte[] hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        string challenge = Base64UrlEncode(hash);
        return (verifier, challenge);
    }

    private static string? GetFragmentParam(string fragment, string name)
    {
        var m = Regex.Match(fragment, $@"(?:^|&){Regex.Escape(name)}=([^&]*)");
        return m.Success ? Uri.UnescapeDataString(m.Groups[1].Value) : null;
    }

    private static string ExtractConfigJson(string html)
    {
        int idx = html.IndexOf("$Config=", StringComparison.Ordinal);
        if (idx < 0) throw new InvalidOperationException("Could not find $Config on the authorize/sign-in page. Page snippet: " + html[..Math.Min(500, html.Length)]);
        int start = html.IndexOf('{', idx);
        if (start < 0) throw new InvalidOperationException("Could not find $Config JSON start brace.");
        int depth = 0;
        bool inString = false, escape = false;
        int i = start;
        for (; i < html.Length; i++)
        {
            char c = html[i];
            if (inString)
            {
                if (escape) escape = false;
                else if (c == '\\') escape = true;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) { i++; break; }
            }
        }
        return html.Substring(start, i - start);
    }

    private static string? ExtractNamedInputValue(string html, string name)
    {
        var m = Regex.Match(html, $@"name=[""']{Regex.Escape(name)}[""']\s+value=[""']([^""']*)[""']", RegexOptions.Singleline);
        if (!m.Success)
            m = Regex.Match(html, $@"value=[""']([^""']*)[""']\s+name=[""']{Regex.Escape(name)}[""']", RegexOptions.Singleline);
        return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value) : null;
    }

    private static string? ExtractFormAction(string html)
    {
        var m = Regex.Match(html, @"<form\b[^>]*?action=[""']([^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value) : null;
    }

    private static string? TryBuildSessionAwareUrlLogin(JsonElement cfg, string urlLogin)
    {
        if (!cfg.TryGetProperty("arrSessions", out var arrSessionsEl) || arrSessionsEl.ValueKind != JsonValueKind.Array)
            return null;

        var aadSignedInSessions = arrSessionsEl.EnumerateArray()
            .Where(s =>
                s.ValueKind == JsonValueKind.Object &&
                s.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String &&
                s.TryGetProperty("idp", out var idpEl) && idpEl.ValueKind == JsonValueKind.Number && idpEl.GetInt32() == 0 && // SessionIdp.Aad == 0
                ((s.TryGetProperty("isSignedIn", out var signedInEl) && signedInEl.ValueKind == JsonValueKind.True) ||
                 (s.TryGetProperty("isWindowsSso", out var winSsoEl) && winSsoEl.ValueKind == JsonValueKind.True)))
            .ToList();

        if (aadSignedInSessions.Count != 1)
            return null;

        string sessionId = aadSignedInSessions[0].GetProperty("id").GetString()!;
        string separator = urlLogin.Contains('?') ? "&" : "?";
        return urlLogin + separator + "sessionid=" + Uri.EscapeDataString(sessionId);
    }

    private static string RequireProp(JsonElement root, string name, string contextForError)
    {
        if (root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String)
            return el.GetString()!;
        var availableKeys = string.Join(", ", root.EnumerateObject().Select(p => p.Name).Take(40));
        throw new InvalidOperationException(
            $"Expected string property '{name}' not found in {contextForError}. Available top-level keys: {availableKeys}");
    }

    public async Task<OutlookOAuthTokens> LoginAsync(string email, string password)
    {
        void Dump(string name, string content) => _dumper?.Dump(name, content);

        string correlationId = Guid.NewGuid().ToString();
        string nonce = Guid.NewGuid().ToString();
        string state = Guid.NewGuid().ToString("N");
        var (codeVerifier, codeChallenge) = GeneratePkce();

        Log("=== STEP 1: GET MSAL SPA /authorize page ===");
        string authorizeUrl =
            "https://login.microsoftonline.com/common/oauth2/v2.0/authorize" +
            $"?client_id={ClientId}" +
            $"&scope={Uri.EscapeDataString(Scope)}" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
            $"&client-request-id={correlationId}" +
            "&response_mode=fragment" +
            "&client_info=1" +
            "&prompt=select_account" +
            $"&nonce={nonce}" +
            $"&state={state}" +
            "&x-client-SKU=msal.js.browser" +
            "&x-client-VER=5.12.0" +
            "&response_type=code" +
            $"&code_challenge={codeChallenge}" +
            "&code_challenge_method=S256";

        var req1 = new HttpRequestMessage(HttpMethod.Get, authorizeUrl);
        req1.Headers.Referrer = new Uri("https://outlook.live.com/");
        var resp1 = await _http.SendAsync(req1);
        string body1 = await resp1.Content.ReadAsStringAsync();
        Dump("01_authorize.html", body1);
        if (!resp1.IsSuccessStatusCode)
            throw new InvalidOperationException($"Authorize page failed (status {(int)resp1.StatusCode}). Body: {body1[..Math.Min(500, body1.Length)]}");
        string configJson1 = ExtractConfigJson(body1);
        Dump("01_config.json", configJson1);
        using var cfg1Doc = JsonDocument.Parse(configJson1);
        var cfg1 = cfg1Doc.RootElement;
        LogCookieJar("after STEP 1 (authorize)");

        // Fallback safety net: even with the sentinel cookies pre-seeded, still
        // follow a BssoInterrupt hop if the server serves one anyway.
        int bssoHops = 0;
        while (!cfg1.TryGetProperty("sFT", out _) && cfg1.TryGetProperty("urlPost", out var urlPostEl) && bssoHops < 3)
        {
            bssoHops++;
            string urlPost = urlPostEl.GetString()!;
            string nextUrl = urlPost.StartsWith("http") ? urlPost : new Uri(new Uri(authorizeUrl), urlPost).ToString();
            Log($"    (BssoInterrupt hop {bssoHops}: no sFT yet, following urlPost -> {nextUrl[..Math.Min(90, nextUrl.Length)]}...)");
            var respHop = await _http.GetAsync(nextUrl);
            string bodyHop = await respHop.Content.ReadAsStringAsync();
            Dump($"01_authorize_hop{bssoHops}.html", bodyHop);
            if (!respHop.IsSuccessStatusCode)
                throw new InvalidOperationException($"BssoInterrupt follow-up hop {bssoHops} failed (status {(int)respHop.StatusCode}).");
            string configJsonHop = ExtractConfigJson(bodyHop);
            Dump($"01_config_hop{bssoHops}.json", configJsonHop);
            cfg1 = JsonDocument.Parse(configJsonHop).RootElement;
        }

        string sFT = RequireProp(cfg1, "sFT", "authorize page $Config (after following any BssoInterrupt hops)");
        string sCtx = RequireProp(cfg1, "sCtx", "authorize page $Config (after following any BssoInterrupt hops)");
        string apiCanary = RequireProp(cfg1, "apiCanary", "authorize page $Config (after following any BssoInterrupt hops)");
        string sessionId = RequireProp(cfg1, "sessionId", "authorize page $Config (after following any BssoInterrupt hops)");
        int hpgact = cfg1.TryGetProperty("hpgact", out var hpgactEl) ? hpgactEl.GetInt32() : 1800;
        int hpgid = cfg1.TryGetProperty("hpgid", out var hpgidEl) ? hpgidEl.GetInt32() : 1104;

        Log("=== STEP 2: POST /common/GetCredentialType (federation discovery) ===");
        var gctBody = new
        {
            username = email,
            isOtherIdpSupported = true,
            checkPhones = true,
            isRemoteNGCSupported = true,
            isCookieBannerShown = false,
            isFidoSupported = true,
            originalRequest = sCtx,
            country = "JP",
            forceotclogin = false,
            isExternalFederationDisallowed = false,
            isRemoteConnectSupported = false,
            federationFlags = 0,
            isSignup = false,
            flowToken = sFT,
            isAccessPassSupported = true,
            isQrCodePinSupported = true,
        };
        var req2 = new HttpRequestMessage(HttpMethod.Post, "https://login.microsoftonline.com/common/GetCredentialType?mkt=ja")
        {
            Content = new StringContent(JsonSerializer.Serialize(gctBody), Encoding.UTF8, "application/json")
        };
        req2.Headers.Add("Accept", "application/json");
        req2.Headers.Add("canary", apiCanary);
        req2.Headers.Add("client-request-id", correlationId);
        req2.Headers.Add("hpgact", hpgact.ToString());
        req2.Headers.Add("hpgid", hpgid.ToString());
        req2.Headers.Add("hpgrequestid", sessionId);
        req2.Headers.Referrer = new Uri(authorizeUrl);
        var resp2 = await _http.SendAsync(req2);
        string body2 = await resp2.Content.ReadAsStringAsync();
        Dump("02_getcredentialtype.json", body2);
        if (!resp2.IsSuccessStatusCode)
            throw new InvalidOperationException($"GetCredentialType failed (status {(int)resp2.StatusCode}). Body: {body2}");
        using var doc2 = JsonDocument.Parse(body2);
        if (!doc2.RootElement.TryGetProperty("Credentials", out var credsEl) ||
            !credsEl.TryGetProperty("FederationRedirectUrl", out var fedUrlEl) ||
            fedUrlEl.GetString() is not string federationRedirectUrl)
        {
            // Not a federated account -> would need the standard AAD password-post flow instead (not implemented here).
            throw new InvalidOperationException(
                "This account is NOT federated to an on-prem ADFS (no FederationRedirectUrl returned). " +
                "The OutlookAdfsLoginClient only implements the ADFS-federated path. Body: " + body2);
        }
        Log($"    Federated. ADFS redirect: {federationRedirectUrl[..Math.Min(90, federationRedirectUrl.Length)]}...");

        Log("=== STEP 3: GET ADFS login page ===");
        var resp3 = await _http.GetAsync(federationRedirectUrl);
        string body3 = await resp3.Content.ReadAsStringAsync();
        Dump("03_adfs_login.html", body3);
        if (!resp3.IsSuccessStatusCode)
            throw new InvalidOperationException($"ADFS login page GET failed (status {(int)resp3.StatusCode}).");
        string? adfsFormAction = ExtractFormAction(body3);
        if (string.IsNullOrEmpty(adfsFormAction))
            throw new InvalidOperationException("Could not find ADFS login form action. Body snippet: " + body3[..Math.Min(500, body3.Length)]);
        string adfsPostUrl = adfsFormAction.StartsWith("http")
            ? adfsFormAction
            : new Uri(new Uri(federationRedirectUrl), adfsFormAction).ToString();

        Log("=== STEP 4: POST username+password to ADFS ===");
        var adfsFormBody = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["UserName"] = email,
            ["Password"] = password,
            ["AuthMethod"] = "FormsAuthentication",
        });
        var req4 = new HttpRequestMessage(HttpMethod.Post, adfsPostUrl) { Content = adfsFormBody };
        req4.Headers.Referrer = new Uri(federationRedirectUrl);
        req4.Headers.Add("Origin", new Uri(adfsPostUrl).GetLeftPart(UriPartial.Authority));
        var resp4 = await _http.SendAsync(req4);
        if (resp4.StatusCode != HttpStatusCode.Found)
        {
            string errBody = await resp4.Content.ReadAsStringAsync();
            Dump("04_adfs_post_error.html", errBody);
            throw new InvalidOperationException(
                $"ADFS credential POST did not redirect (status {(int)resp4.StatusCode}). " +
                "Most likely cause: WRONG USERNAME/PASSWORD, or MFA is being requested by ADFS (not handled by this client). " +
                $"Snippet: {errBody[..Math.Min(400, errBody.Length)]}");
        }
        string? adfsRedirectLoc = resp4.Headers.Location?.ToString();
        if (string.IsNullOrEmpty(adfsRedirectLoc))
            throw new InvalidOperationException("ADFS POST returned 302 but no Location header.");

        Log("=== STEP 5: follow ADFS redirect -> get WS-Federation auto-post-back form ===");
        var resp5 = await _http.GetAsync(adfsRedirectLoc);
        string body5 = await resp5.Content.ReadAsStringAsync();
        Dump("05_wsfed_postback.html", body5);
        if (!resp5.IsSuccessStatusCode)
            throw new InvalidOperationException($"ADFS post-auth GET failed (status {(int)resp5.StatusCode}).");
        string? wresult = ExtractNamedInputValue(body5, "wresult");
        string? wa = ExtractNamedInputValue(body5, "wa");
        if (string.IsNullOrEmpty(wresult) || string.IsNullOrEmpty(wa))
            throw new InvalidOperationException(
                "Could not find wresult/wa in ADFS's WS-Federation response page. " +
                "If ADFS is prompting for MFA, this client does not (yet) handle it. Body snippet: " +
                body5[..Math.Min(500, body5.Length)]);
        string? wctx = ExtractNamedInputValue(body5, "wctx");
        string? wsFedPostUrl = ExtractFormAction(body5);
        if (string.IsNullOrEmpty(wsFedPostUrl))
            throw new InvalidOperationException("Could not find WS-Federation auto-post-back form action (login.srf URL).");
        LogCookieJar("after STEP 5 (ADFS redirect, before posting SAML token to AAD)");

        Log("=== STEP 6: POST SAML token to login.microsoftonline.com/login.srf (following any further interrupts) ===");
        string currentPostUrl = wsFedPostUrl;
        var currentFields = new Dictionary<string, string> { ["wa"] = wa, ["wresult"] = wresult };
        if (!string.IsNullOrEmpty(wctx)) currentFields["wctx"] = wctx;
        string[] wsFedFieldNames = { "wa", "wresult", "wctx" };
        string? finalLoc = null;
        int wsFedHops = 0;
        while (wsFedHops < 5)
        {
            wsFedHops++;
            var reqPost = new HttpRequestMessage(HttpMethod.Post, currentPostUrl) { Content = new FormUrlEncodedContent(currentFields) };
            var postOrigin = new Uri(currentPostUrl).GetLeftPart(UriPartial.Authority);
            reqPost.Headers.Referrer = new Uri(postOrigin + "/");
            reqPost.Headers.Add("Origin", postOrigin);
            var respPost = await _http.SendAsync(reqPost);
            if (respPost.StatusCode == HttpStatusCode.Found)
            {
                finalLoc = respPost.Headers.Location?.ToString();
                LogCookieJar($"after STEP 6 hop {wsFedHops} (got final 302 redirect)");
                break;
            }
            string bodyPost = await respPost.Content.ReadAsStringAsync();
            Dump($"06_wsfed_hop{wsFedHops}.html", bodyPost);
            if (!respPost.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"WS-Federation post-back hop {wsFedHops} to {currentPostUrl} failed (status {(int)respPost.StatusCode}). " +
                    $"Body: {bodyPost[..Math.Min(400, bodyPost.Length)]}");

            string configJsonPost = ExtractConfigJson(bodyPost);
            Dump($"06_config_hop{wsFedHops}.json", configJsonPost);
            using var cfgPostDoc = JsonDocument.Parse(configJsonPost);
            var cfgPost = cfgPostDoc.RootElement;

            string pgid = cfgPost.TryGetProperty("pgid", out var pgidEl) ? (pgidEl.GetString() ?? "") : "";

            if (pgid == "KmsiInterrupt" ||
                (cfgPost.TryGetProperty("urlPost", out var kmsiUrlEl) && kmsiUrlEl.GetString() == "/kmsi"))
            {
                Log($"    (hop {wsFedHops}: got the \"Keep me signed in?\" (KMSI) prompt -- login already succeeded and AAD session cookies are set. Bypassing the broken /kmsi post-back and doing a silent re-authorize instead...)");
                LogCookieJar("at KMSI prompt (i.e. right after AAD validated the SAML assertion)");

                string silentAuthorizeUrl = authorizeUrl.Replace("prompt=select_account", "prompt=none");
                if (!silentAuthorizeUrl.Contains("login_hint="))
                    silentAuthorizeUrl += $"&login_hint={Uri.EscapeDataString(email)}";
                var reqSilent = new HttpRequestMessage(HttpMethod.Get, silentAuthorizeUrl);
                reqSilent.Headers.Referrer = new Uri("https://outlook.live.com/");
                var respSilent = await _http.SendAsync(reqSilent);
                if (respSilent.StatusCode == HttpStatusCode.Found)
                {
                    finalLoc = respSilent.Headers.Location?.ToString();
                    LogCookieJar("after silent re-authorize (prompt=none+login_hint) -- got 302 redirect");
                    break;
                }

                string bodySilent = await respSilent.Content.ReadAsStringAsync();
                Dump("06b_silent_reauthorize.html", bodySilent);

                string pgidSilent = "";
                try
                {
                    string configJsonSilent = ExtractConfigJson(bodySilent);
                    Dump("06b_config_silent_reauthorize.json", configJsonSilent);
                    using var cfgSilentDoc = JsonDocument.Parse(configJsonSilent);
                    var cfgSilent = cfgSilentDoc.RootElement;
                    pgidSilent = cfgSilent.TryGetProperty("pgid", out var pgidSilentEl) ? (pgidSilentEl.GetString() ?? "") : "";

                    if (cfgSilent.TryGetProperty("urlLogin", out var urlLoginEl) && urlLoginEl.GetString() is string urlLogin)
                    {
                        var cfgForSessionLookup = cfgSilent;
                        string currentUrlLogin = urlLogin;
                        string? bodyReprocess = null;
                        int fetchSessionsHops = 0;
                        while (fetchSessionsHops < 5)
                        {
                            fetchSessionsHops++;
                            string urlLoginToFollow = TryBuildSessionAwareUrlLogin(cfgForSessionLookup, currentUrlLogin) ?? currentUrlLogin;
                            Log($"    (FetchSessions hop {fetchSessionsHops}: following urlLogin/reprocess (session-aware={urlLoginToFollow != currentUrlLogin}) -> {urlLoginToFollow[..Math.Min(110, urlLoginToFollow.Length)]}...)");
                            var reqReprocess = new HttpRequestMessage(HttpMethod.Get, urlLoginToFollow);
                            reqReprocess.Headers.Referrer = new Uri(silentAuthorizeUrl);
                            var respReprocess = await _http.SendAsync(reqReprocess);
                            if (respReprocess.StatusCode == HttpStatusCode.Found)
                            {
                                finalLoc = respReprocess.Headers.Location?.ToString();
                                LogCookieJar($"after following FetchSessions' urlLogin/reprocess hop {fetchSessionsHops} -- got 302 redirect");
                                break;
                            }
                            bodyReprocess = await respReprocess.Content.ReadAsStringAsync();
                            Dump($"06c_reprocess_hop{fetchSessionsHops}.html", bodyReprocess);
                            if (!respReprocess.IsSuccessStatusCode)
                                throw new InvalidOperationException(
                                    $"FetchSessions urlLogin/reprocess hop {fetchSessionsHops} failed (status {(int)respReprocess.StatusCode}). " +
                                    $"Body snippet: {bodyReprocess[..Math.Min(500, bodyReprocess.Length)]}");

                            // Not a redirect yet -- see if this is yet another
                            // FetchSessions page (fresh ctx) we should follow again.
                            string configJsonReprocess = ExtractConfigJson(bodyReprocess);
                            Dump($"06c_config_reprocess_hop{fetchSessionsHops}.json", configJsonReprocess);
                            using var cfgReprocessDoc = JsonDocument.Parse(configJsonReprocess);
                            cfgForSessionLookup = cfgReprocessDoc.RootElement;
                            if (!cfgForSessionLookup.TryGetProperty("urlLogin", out var nextUrlLoginEl) || nextUrlLoginEl.GetString() is not string nextUrlLogin)
                                throw new InvalidOperationException(
                                    $"FetchSessions urlLogin/reprocess hop {fetchSessionsHops} returned a non-redirect page with no further urlLogin to follow. " +
                                    $"Body snippet: {bodyReprocess[..Math.Min(500, bodyReprocess.Length)]}");
                            currentUrlLogin = nextUrlLogin;
                        }
                        if (finalLoc != null) break;
                        LogCookieJar("after following FetchSessions' urlLogin/reprocess -- did NOT get a 302 redirect after max hops");
                        throw new InvalidOperationException(
                            $"Followed FetchSessions' urlLogin/reprocess URL for {fetchSessionsHops} hop(s) but still did not get the expected redirect. " +
                            $"Last body snippet: {bodyReprocess?[..Math.Min(500, bodyReprocess.Length)]}");
                    }
                }
                catch (InvalidOperationException) { throw; }
                catch {  }

                LogCookieJar("after silent re-authorize (prompt=none+login_hint) -- did NOT get a 302 redirect");
                throw new InvalidOperationException(
                    $"Silent re-authorize (prompt=none+login_hint) after KMSI did not produce the expected redirect (status {(int)respSilent.StatusCode}, pgid={pgidSilent}). " +
                    "This likely means Conditional Access/MFA is being enforced for silent renewal, or the session cookies were not accepted. " +
                    $"Body snippet: {bodySilent[..Math.Min(500, bodySilent.Length)]}");
            }

            if (!cfgPost.TryGetProperty("urlPost", out var urlPostHopEl) ||
                !cfgPost.TryGetProperty("oPostParams", out var postParamsEl))
            {
                LogCookieJar("at final FAILURE point (non-redirect, non-BssoInterrupt/KMSI response)");
                throw new InvalidOperationException(
                    "Got a non-redirect (200) response with no recognizable BssoInterrupt/KMSI (urlPost/oPostParams) to follow. " +
                    $"Body snippet: {bodyPost[..Math.Min(500, bodyPost.Length)]}");
            }

            string urlPostHop = urlPostHopEl.GetString()!;
            currentPostUrl = urlPostHop.StartsWith("http") ? urlPostHop : new Uri(new Uri(currentPostUrl), urlPostHop).ToString();
            currentFields = new Dictionary<string, string>();
            foreach (var fieldName in wsFedFieldNames)
            {
                if (postParamsEl.TryGetProperty(fieldName, out var fieldEl) && fieldEl.ValueKind == JsonValueKind.String)
                    currentFields[fieldName] = WebUtility.HtmlDecode(fieldEl.GetString()!);
            }
            Log($"    (WS-Fed post-back hop {wsFedHops}: got another interrupt page, following urlPost -> {currentPostUrl[..Math.Min(90, currentPostUrl.Length)]}...)");
        }
        if (string.IsNullOrEmpty(finalLoc) || !finalLoc.Contains('#'))
            throw new InvalidOperationException($"WS-Federation post-back never produced a redirect to outlook.live.com with a code fragment (after {wsFedHops} hop(s)). Last Location: {finalLoc}");

        Log("=== STEP 7: parse authorization code out of the redirect fragment ===");
        string fragment = finalLoc[(finalLoc.IndexOf('#') + 1)..];
        string? code = GetFragmentParam(fragment, "code");
        if (string.IsNullOrEmpty(code))
            throw new InvalidOperationException($"No 'code' found in redirect fragment: {fragment[..Math.Min(300, fragment.Length)]}");

        Log("=== STEP 8: POST /common/oauth2/v2.0/token (authorization_code + PKCE verifier) ===");
        var tokenFields = new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["redirect_uri"] = RedirectUri,
            ["scope"] = Scope,
            ["code"] = code,
            ["code_verifier"] = codeVerifier,
            ["grant_type"] = "authorization_code",
            ["client_info"] = "1",
        };
        var req8 = new HttpRequestMessage(HttpMethod.Post, "https://login.microsoftonline.com/common/oauth2/v2.0/token")
        { Content = new FormUrlEncodedContent(tokenFields) };
        req8.Headers.Referrer = new Uri("https://outlook.live.com/");
        req8.Headers.Add("Origin", "https://outlook.live.com");
        var resp8 = await _http.SendAsync(req8);
        string body8 = await resp8.Content.ReadAsStringAsync();
        Dump("08_token.json", body8);
        if (!resp8.IsSuccessStatusCode)
            throw new InvalidOperationException($"Token exchange failed (status {(int)resp8.StatusCode}). Body: {body8}");

        using var doc8 = JsonDocument.Parse(body8);
        var tokens = new OutlookOAuthTokens
        {
            AccessToken = doc8.RootElement.GetProperty("access_token").GetString()!,
            RefreshToken = doc8.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null,
            IdToken = doc8.RootElement.TryGetProperty("id_token", out var it) ? it.GetString() : null,
            ExpiresIn = doc8.RootElement.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 0,
            RawTokenResponse = body8,
        };
        Log($"=== Login complete. access_token acquired (expires_in={tokens.ExpiresIn}s). ===");
        return tokens;
    }

    public async Task<string> FetchInboxViaLegacyRestApiAsync(string accessToken, int top = 10)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"https://outlook.office.com/api/v2.0/me/messages?$top={top}");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        req.Headers.Add("Accept", "application/json");
        var resp = await _http.SendAsync(req);
        string body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Legacy Outlook REST API v2.0 /messages call failed (status {(int)resp.StatusCode}). Body: {body}");
        return body;
    }

    public async Task<string> FetchInboxViaEwsAsync(string accessToken, string userPrincipalName, int top = 25)
    {
        string soapBody =
$@"<?xml version=""1.0"" encoding=""utf-8""?>
<soap:Envelope xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/""
               xmlns:t=""http://schemas.microsoft.com/exchange/services/2006/types""
               xmlns:m=""http://schemas.microsoft.com/exchange/services/2006/messages"">
  <soap:Header>
    <t:RequestServerVersion Version=""Exchange2016"" />
  </soap:Header>
  <soap:Body>
    <m:FindItem Traversal=""Shallow"">
      <m:ItemShape>
        <t:BaseShape>Default</t:BaseShape>
      </m:ItemShape>
      <m:IndexedPageItemView MaxEntriesReturned=""{top}"" Offset=""0"" BasePoint=""Beginning"" />
      <m:SortOrder>
        <t:FieldOrder Order=""Descending"">
          <t:FieldURI FieldURI=""item:DateTimeReceived"" />
        </t:FieldOrder>
      </m:SortOrder>
      <m:ParentFolderIds>
        <t:DistinguishedFolderId Id=""inbox"" />
      </m:ParentFolderIds>
    </m:FindItem>
  </soap:Body>
</soap:Envelope>";

        var req = new HttpRequestMessage(HttpMethod.Post, "https://outlook.office365.com/EWS/Exchange.asmx")
        {
            Content = new StringContent(soapBody, Encoding.UTF8, "text/xml"),
        };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        // Recommended for OAuth-authenticated EWS calls against Exchange Online:
        // tells the service which mailbox to route to without an extra AD lookup.
        req.Headers.Add("X-AnchorMailbox", userPrincipalName);
        var resp = await _http.SendAsync(req);
        string body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"EWS FindItem call failed (status {(int)resp.StatusCode}). Body: {body}");
        return body;
    }

    public static List<(string Subject, string From, string Received)> ParseEwsMessageSummaries(string ewsXml)
    {
        var results = new List<(string, string, string)>();
        var subjectMatches = Regex.Matches(ewsXml, @"<t:Subject>(.*?)</t:Subject>", RegexOptions.Singleline);
        var dateMatches = Regex.Matches(ewsXml, @"<t:DateTimeReceived>(.*?)</t:DateTimeReceived>", RegexOptions.Singleline);
        var fromMatches = Regex.Matches(ewsXml, @"<t:From><t:Mailbox><t:Name>(.*?)</t:Name>", RegexOptions.Singleline);
        int n = subjectMatches.Count;
        for (int i = 0; i < n; i++)
        {
            string subj = WebUtility.HtmlDecode(subjectMatches[i].Groups[1].Value);
            string date = i < dateMatches.Count ? dateMatches[i].Groups[1].Value : "";
            string from = i < fromMatches.Count ? WebUtility.HtmlDecode(fromMatches[i].Groups[1].Value) : "";
            results.Add((subj, from, date));
        }
        return results;
    }

    public void Dispose() => _http.Dispose();
}
