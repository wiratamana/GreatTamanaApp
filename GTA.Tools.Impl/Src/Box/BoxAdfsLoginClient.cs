
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GTA.Tools.Impl.Common;

namespace GTA.Tools.Impl.Box;

/// <summary>One item (file or folder) as returned by Box's internal GraphQL folder-listing query.</summary>
public sealed class BoxItem
{
    public string TypedId { get; set; } = "";
    public string Id { get; set; } = "";
    public string Type { get; set; } = ""; // "file" | "folder" | "weblink"
    public string Name { get; set; } = "";
    public long? Size { get; set; }
    public string? Extension { get; set; }
}

public sealed class BoxAdfsLoginClient : IDisposable
{
    // Tenant-specific hosts (reverse-engineered from the captured log -- see header).
    private readonly string _boxHost;
    private readonly string _accountHost;

    private readonly HttpClientHandler _handler;
    private readonly HttpClient _http;
    private readonly Action<string>? _log;
    private readonly IDiagnosticDumper? _dumper;

    /// <summary>True once LoginAsync() has completed successfully.</summary>
    public bool IsLoggedIn { get; private set; }

    public BoxAdfsLoginClient(
        string boxHost,
        string accountHost,
        Action<string>? logger = null,
        IDiagnosticDumper? dumper = null,
        bool useSystemProxyWithDefaultCredentials = true)
    {
        _boxHost = boxHost;
        _accountHost = accountHost;
        _log = logger;
        _dumper = dumper;

        _handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true,
            AllowAutoRedirect = false,
        };

        // Same sentinel-cookie pre-seed trick as OutlookAdfsLoginClient: short-circuits
        // AAD's "BssoInterrupt" (client-JS-only) page on a brand-new, cookie-less client.
        _handler.CookieContainer.Add(new Cookie("SSOCOOKIEPULLED", "1", "/", "login.microsoftonline.com"));
        _handler.CookieContainer.Add(new Cookie("AADSSO", "NA|NoExtension", "/", "login.microsoftonline.com"));

        if (useSystemProxyWithDefaultCredentials)
        {
            _handler.UseProxy = true;
            _handler.DefaultProxyCredentials = CredentialCache.DefaultCredentials;
        }

        _http = new HttpClient(_handler) { Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Add("Accept-Language", "ja,en-US;q=0.9,en;q=0.8");
    }

    private void Log(string msg) => _log?.Invoke(msg);
    private void Dump(string name, string content) => _dumper?.Dump(name, content);

    private static string ExtractConfigJson(string html)
    {
        int idx = html.IndexOf("$Config=", StringComparison.Ordinal);
        if (idx < 0) throw new InvalidOperationException("Could not find $Config on the page. Snippet: " + html[..Math.Min(500, html.Length)]);
        int start = html.IndexOf('{', idx);
        if (start < 0) throw new InvalidOperationException("Could not find $Config JSON start brace.");
        int depth = 0; bool inString = false, escape = false; int i = start;
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
            else if (c == '}') { depth--; if (depth == 0) { i++; break; } }
        }
        return html.Substring(start, i - start);
    }

    private static bool TryExtractConfigJson(string html, out string json)
    {
        try { json = ExtractConfigJson(html); return true; }
        catch { json = ""; return false; }
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

    private static bool ContainsSamlResponse(string html) => ExtractNamedInputValue(html, "SAMLResponse") != null;

    private static string RequireProp(JsonElement root, string name, string context)
    {
        if (root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String) return el.GetString()!;
        throw new InvalidOperationException($"Expected string property '{name}' not found in {context}.");
    }

    private static string ResolveUrl(string baseUrl, string maybeRelative) =>
        maybeRelative.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? maybeRelative
            : new Uri(new Uri(baseUrl), maybeRelative).ToString();

    private async Task<(string finalUrl, string html)> FollowRedirectsAsync(string url, int maxHops = 6)
    {
        string current = url;
        for (int hop = 0; hop < maxHops; hop++)
        {
            var resp = await _http.GetAsync(current);
            if (resp.StatusCode is HttpStatusCode.Found or HttpStatusCode.Redirect or HttpStatusCode.Moved or HttpStatusCode.MovedPermanently)
            {
                string? loc = resp.Headers.Location?.ToString();
                if (string.IsNullOrEmpty(loc))
                    throw new InvalidOperationException($"Redirect from {current} had no Location header.");
                current = ResolveUrl(current, loc);
                continue;
            }
            string body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"GET {current} failed (status {(int)resp.StatusCode}). Body: {body[..Math.Min(300, body.Length)]}");
            return (current, body);
        }
        throw new InvalidOperationException($"Too many redirects (> {maxHops}) starting from {url}.");
    }

    private async Task<string> GetFederationRedirectUrlAsync(string email, string sFT, string sCtx, string apiCanary, string correlationId, string authorizeLikeUrl)
    {
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
        var req = new HttpRequestMessage(HttpMethod.Post, "https://login.microsoftonline.com/common/GetCredentialType?mkt=ja")
        {
            Content = new StringContent(JsonSerializer.Serialize(gctBody), Encoding.UTF8, "application/json")
        };
        req.Headers.Add("Accept", "application/json");
        req.Headers.Add("canary", apiCanary);
        req.Headers.Add("client-request-id", correlationId);
        req.Headers.Referrer = new Uri(authorizeLikeUrl);
        var resp = await _http.SendAsync(req);
        string body = await resp.Content.ReadAsStringAsync();
        Dump("GetCredentialType.json", body);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"GetCredentialType failed (status {(int)resp.StatusCode}). Body: {body}");
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("Credentials", out var credsEl) ||
            !credsEl.TryGetProperty("FederationRedirectUrl", out var fedUrlEl) ||
            fedUrlEl.GetString() is not string url)
            throw new InvalidOperationException("This account is NOT federated to an on-prem ADFS (no FederationRedirectUrl). Body: " + body);
        return url;
    }

    private async Task<string> DoAdfsLoginAndResolveSamlAsync(string email, string password, string federationRedirectUrl, string entrySamlUrl)
    {
        Log("    (Federated account detected -- performing full ADFS username+password login...)");

        var respAdfsPage = await _http.GetAsync(federationRedirectUrl);
        string adfsPageBody = await respAdfsPage.Content.ReadAsStringAsync();
        Dump("adfs_login_page.html", adfsPageBody);
        if (!respAdfsPage.IsSuccessStatusCode)
            throw new InvalidOperationException($"ADFS login page GET failed (status {(int)respAdfsPage.StatusCode}).");
        string? adfsFormAction = ExtractFormAction(adfsPageBody);
        if (string.IsNullOrEmpty(adfsFormAction))
            throw new InvalidOperationException("Could not find ADFS login form action. Snippet: " + adfsPageBody[..Math.Min(400, adfsPageBody.Length)]);
        string adfsPostUrl = ResolveUrl(federationRedirectUrl, adfsFormAction);

        var adfsFormBody = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["UserName"] = email,
            ["Password"] = password,
            ["AuthMethod"] = "FormsAuthentication",
        });
        var reqAdfsPost = new HttpRequestMessage(HttpMethod.Post, adfsPostUrl) { Content = adfsFormBody };
        reqAdfsPost.Headers.Referrer = new Uri(federationRedirectUrl);
        reqAdfsPost.Headers.Add("Origin", new Uri(adfsPostUrl).GetLeftPart(UriPartial.Authority));
        var respAdfsPost = await _http.SendAsync(reqAdfsPost);
        if (respAdfsPost.StatusCode != HttpStatusCode.Found)
        {
            string errBody = await respAdfsPost.Content.ReadAsStringAsync();
            Dump("adfs_post_error.html", errBody);
            throw new InvalidOperationException(
                $"ADFS credential POST did not redirect (status {(int)respAdfsPost.StatusCode}). " +
                "Most likely cause: wrong username/password, or MFA is being requested by ADFS (not handled here). " +
                $"Snippet: {errBody[..Math.Min(400, errBody.Length)]}");
        }
        string? adfsRedirectLoc = respAdfsPost.Headers.Location?.ToString();
        if (string.IsNullOrEmpty(adfsRedirectLoc))
            throw new InvalidOperationException("ADFS POST returned 302 but no Location header.");

        var respWsFed = await _http.GetAsync(ResolveUrl(adfsPostUrl, adfsRedirectLoc));
        string wsFedBody = await respWsFed.Content.ReadAsStringAsync();
        Dump("wsfed_postback_page.html", wsFedBody);
        if (!respWsFed.IsSuccessStatusCode)
            throw new InvalidOperationException($"ADFS post-auth GET failed (status {(int)respWsFed.StatusCode}).");
        string? wresult = ExtractNamedInputValue(wsFedBody, "wresult");
        string? wa = ExtractNamedInputValue(wsFedBody, "wa");
        if (string.IsNullOrEmpty(wresult) || string.IsNullOrEmpty(wa))
            throw new InvalidOperationException(
                "Could not find wresult/wa in ADFS's WS-Federation response page (MFA not handled here). Snippet: " +
                wsFedBody[..Math.Min(500, wsFedBody.Length)]);
        string? wctx = ExtractNamedInputValue(wsFedBody, "wctx");
        string? wsFedPostUrl = ExtractFormAction(wsFedBody);
        if (string.IsNullOrEmpty(wsFedPostUrl))
            throw new InvalidOperationException("Could not find WS-Federation auto-post-back form action.");

        // Now resubmit wa/wresult/wctx to AAD, following any further interrupts,
        // until we land on a page containing SAMLResponse.
        string currentPostUrl = ResolveUrl(adfsPostUrl, wsFedPostUrl);
        var currentFields = new Dictionary<string, string> { ["wa"] = wa, ["wresult"] = wresult };
        if (!string.IsNullOrEmpty(wctx)) currentFields["wctx"] = wctx;
        string[] wsFedFieldNames = { "wa", "wresult", "wctx" };

        for (int hop = 0; hop < 8; hop++)
        {
            var reqPost = new HttpRequestMessage(HttpMethod.Post, currentPostUrl) { Content = new FormUrlEncodedContent(currentFields) };
            string postOrigin = new Uri(currentPostUrl).GetLeftPart(UriPartial.Authority);
            reqPost.Headers.Referrer = new Uri(postOrigin + "/");
            reqPost.Headers.Add("Origin", postOrigin);
            var respPost = await _http.SendAsync(reqPost);

            if (respPost.StatusCode == HttpStatusCode.Found)
            {
                string? loc = respPost.Headers.Location?.ToString();
                if (string.IsNullOrEmpty(loc)) throw new InvalidOperationException("WS-Fed postback 302 had no Location header.");
                return await ResolveAadPageToSamlResponseAsync(ResolveUrl(currentPostUrl, loc), entrySamlUrl, isFollowUpGet: true);
            }

            string bodyPost = await respPost.Content.ReadAsStringAsync();
            Dump($"wsfed_hop{hop}.html", bodyPost);
            if (!respPost.IsSuccessStatusCode)
                throw new InvalidOperationException($"WS-Federation post-back hop {hop} to {currentPostUrl} failed (status {(int)respPost.StatusCode}).");

            if (ContainsSamlResponse(bodyPost))
                return bodyPost; // SUCCESS

            if (!TryExtractConfigJson(bodyPost, out string cfgJson))
                throw new InvalidOperationException("WS-Fed postback returned a page with neither SAMLResponse nor a recognizable $Config. Snippet: " + bodyPost[..Math.Min(400, bodyPost.Length)]);

            using var cfgDoc = JsonDocument.Parse(cfgJson);
            var cfg = cfgDoc.RootElement;
            string pgid = cfg.TryGetProperty("pgid", out var pgidEl) ? (pgidEl.GetString() ?? "") : "";

            if (pgid == "KmsiInterrupt" || (cfg.TryGetProperty("urlPost", out var kmsiUrlEl) && kmsiUrlEl.GetString() == "/kmsi"))
            {
                Log("    (Got \"Keep me signed in?\" (KMSI) -- login succeeded. Re-fetching the original SAML entry URL to complete silently...)");
                return await ResolveAadPageToSamlResponseAsync(entrySamlUrl, entrySamlUrl, isFollowUpGet: true);
            }

            if (!cfg.TryGetProperty("sFT", out _) && cfg.TryGetProperty("urlPost", out var urlPostEl))
            {
                // BssoInterrupt-style hop: follow urlPost and resubmit nothing (GET).
                string nextUrl = ResolveUrl(currentPostUrl, urlPostEl.GetString()!);
                return await ResolveAadPageToSamlResponseAsync(nextUrl, entrySamlUrl, isFollowUpGet: true);
            }

            throw new InvalidOperationException("WS-Fed postback returned an unrecognized interstitial page (pgid=" + pgid + ").");
        }

        throw new InvalidOperationException("WS-Federation postback loop exceeded max hops without reaching a SAMLResponse.");
    }

    private async Task<string> ResolveAadPageToSamlResponseAsync(string url, string entrySamlUrl, bool isFollowUpGet, string? email = null, string? password = null, int depth = 0)
    {
        if (depth > 10) throw new InvalidOperationException("AAD login resolution exceeded max recursion depth.");

        var resp = await _http.GetAsync(url);
        string body = await resp.Content.ReadAsStringAsync();
        Dump($"aad_resolve_depth{depth}.html", body);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"GET {url} failed during AAD resolution (status {(int)resp.StatusCode}).");

        if (ContainsSamlResponse(body))
            return body; // SUCCESS

        if (!TryExtractConfigJson(body, out string cfgJson))
            throw new InvalidOperationException("Page has neither SAMLResponse nor a recognizable $Config during AAD resolution. Snippet: " + body[..Math.Min(400, body.Length)]);

        using var cfgDoc = JsonDocument.Parse(cfgJson);
        var cfg = cfgDoc.RootElement;

        if (!cfg.TryGetProperty("sFT", out _) && cfg.TryGetProperty("urlPost", out var urlPostEl))
        {
            // BssoInterrupt: no credentials needed yet, just follow urlPost.
            string nextUrl = ResolveUrl(url, urlPostEl.GetString()!);
            return await ResolveAadPageToSamlResponseAsync(nextUrl, entrySamlUrl, true, email, password, depth + 1);
        }

        if (cfg.TryGetProperty("sFT", out var sFTEl) && cfg.TryGetProperty("sCtx", out var sCtxEl))
        {
            if (email == null || password == null)
                throw new InvalidOperationException("AAD is asking for credentials again unexpectedly during resolution (possible bad password or MFA challenge not handled).");

            string apiCanary = RequireProp(cfg, "apiCanary", "$Config on credential page");
            string correlationId = cfg.TryGetProperty("correlationId", out var cid) ? (cid.GetString() ?? Guid.NewGuid().ToString()) : Guid.NewGuid().ToString();
            string fedUrl = await GetFederationRedirectUrlAsync(email, sFTEl.GetString()!, sCtxEl.GetString()!, apiCanary, correlationId, url);
            return await DoAdfsLoginAndResolveSamlAsync(email, password, fedUrl, entrySamlUrl);
        }

        throw new InvalidOperationException("Unrecognized $Config shape during AAD resolution (no urlPost, no sFT+sCtx).");
    }

    public async Task LoginAsync(string email, string password)
    {
        Log($"=== STEP 1: GET https://{_boxHost}/ -> follow redirects down to https://{_accountHost}/login ===");
        var (loginPageUrl, loginPageHtml) = await FollowRedirectsAsync($"https://{_boxHost}/");
        Dump("01_account_login_page.html", loginPageHtml);

        string? requestToken = ExtractNamedInputValue(loginPageHtml, "request_token");
        if (string.IsNullOrEmpty(requestToken))
            throw new InvalidOperationException("Could not find request_token on the account.box.com/login page. Snippet: " + loginPageHtml[..Math.Min(400, loginPageHtml.Length)]);
        string? loginFormAction = ExtractFormAction(loginPageHtml);
        string loginPostUrl = string.IsNullOrEmpty(loginFormAction) ? loginPageUrl : ResolveUrl(loginPageUrl, loginFormAction);
        string? redirectUrlField = ExtractNamedInputValue(loginPageHtml, "redirect_url") ?? "/";
        string? loginPageSource = ExtractNamedInputValue(loginPageHtml, "login_page_source") ?? "sso-prompt-login";

        Log("=== STEP 2: POST account.box.com/login (request_token) -> Box's PingFederate SSO broker ===");
        var loginPostBody = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["redirect_url"] = redirectUrlField!,
            ["request_token"] = requestToken!,
            ["login_page_source"] = loginPageSource!,
        });
        var reqLoginPost = new HttpRequestMessage(HttpMethod.Post, loginPostUrl) { Content = loginPostBody };
        reqLoginPost.Headers.Referrer = new Uri(loginPageUrl);
        reqLoginPost.Headers.Add("Origin", "null");
        var respLoginPost = await _http.SendAsync(reqLoginPost);
        if (respLoginPost.StatusCode != HttpStatusCode.Found)
        {
            string b = await respLoginPost.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"account.box.com/login POST did not redirect (status {(int)respLoginPost.StatusCode}). Body: {b[..Math.Min(400, b.Length)]}");
        }
        string? startSsoUrl = respLoginPost.Headers.Location?.ToString();
        if (string.IsNullOrEmpty(startSsoUrl)) throw new InvalidOperationException("account.box.com/login POST returned 302 with no Location header.");
        startSsoUrl = ResolveUrl(loginPostUrl, startSsoUrl);

        Log("=== STEP 3: GET startSSO.ping -> Azure AD saml2 entry URL ===");
        var respStartSso = await _http.GetAsync(startSsoUrl);
        if (respStartSso.StatusCode != HttpStatusCode.Found)
            throw new InvalidOperationException($"startSSO.ping did not redirect (status {(int)respStartSso.StatusCode}).");
        string? samlUrl = respStartSso.Headers.Location?.ToString();
        if (string.IsNullOrEmpty(samlUrl)) throw new InvalidOperationException("startSSO.ping returned 302 with no Location header.");
        samlUrl = ResolveUrl(startSsoUrl, samlUrl);
        Log($"    AAD saml2 entry URL: {samlUrl[..Math.Min(90, samlUrl.Length)]}...");

        Log("=== STEP 4: resolve AAD login (silent SSO if a session already exists, else full ADFS username+password) ===");
        string samlResponseHtml = await ResolveAadPageToSamlResponseAsync(samlUrl, samlUrl, isFollowUpGet: false, email: email, password: password);
        Dump("04_saml_response_page.html", samlResponseHtml);

        string samlResponse = ExtractNamedInputValue(samlResponseHtml, "SAMLResponse")
            ?? throw new InvalidOperationException("Could not extract SAMLResponse from the resolved AAD page.");
        string relayState = ExtractNamedInputValue(samlResponseHtml, "RelayState") ?? "";
        string? acsAction = ExtractFormAction(samlResponseHtml);
        string acsUrl = string.IsNullOrEmpty(acsAction) ? "https://sso.services.box.net/sp/ACS.saml2" : ResolveUrl(samlUrl, acsAction);

        Log("=== STEP 5: POST SAMLResponse to Box's ACS -> PingFederate opentoken auto-post-back ===");
        var acsFields = new Dictionary<string, string> { ["SAMLResponse"] = samlResponse };
        if (!string.IsNullOrEmpty(relayState)) acsFields["RelayState"] = relayState;
        var reqAcs = new HttpRequestMessage(HttpMethod.Post, acsUrl) { Content = new FormUrlEncodedContent(acsFields) };
        reqAcs.Headers.Referrer = new Uri("https://login.microsoftonline.com/");
        reqAcs.Headers.Add("Origin", "https://login.microsoftonline.com");
        var respAcs = await _http.SendAsync(reqAcs);
        string acsBody = await respAcs.Content.ReadAsStringAsync();
        Dump("05_acs_response.html", acsBody);
        if (!respAcs.IsSuccessStatusCode)
            throw new InvalidOperationException($"POST to ACS.saml2 failed (status {(int)respAcs.StatusCode}). Body: {acsBody[..Math.Min(400, acsBody.Length)]}");

        string opentoken = ExtractNamedInputValue(acsBody, "opentoken")
            ?? throw new InvalidOperationException("Could not extract opentoken from ACS response. Snippet: " + acsBody[..Math.Min(400, acsBody.Length)]);
        string? pfAction = ExtractFormAction(acsBody);
        string pingFederateUrl = string.IsNullOrEmpty(pfAction)
            ? $"https://{_accountHost}/sso/ping_federate?from=box&redirect_url=%2F"
            : ResolveUrl(acsUrl, pfAction);

        Log("=== STEP 6: POST opentoken to account.box.com/sso/ping_federate -> ent.box.com/login/assertion ===");
        var reqPf = new HttpRequestMessage(HttpMethod.Post, pingFederateUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["opentoken"] = opentoken })
        };
        reqPf.Headers.Referrer = new Uri("https://sso.services.box.net/");
        reqPf.Headers.Add("Origin", "https://sso.services.box.net");
        var respPf = await _http.SendAsync(reqPf);
        if (respPf.StatusCode != HttpStatusCode.Found)
        {
            string b = await respPf.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"ping_federate POST did not redirect (status {(int)respPf.StatusCode}). Body: {b[..Math.Min(400, b.Length)]}");
        }
        string? assertionUrl = respPf.Headers.Location?.ToString();
        if (string.IsNullOrEmpty(assertionUrl)) throw new InvalidOperationException("ping_federate POST returned 302 with no Location header.");
        assertionUrl = ResolveUrl(pingFederateUrl, assertionUrl);

        Log("=== STEP 7: GET login/assertion -> /folder/0 (real ent.box.com session cookies get set here) ===");
        var respAssertion = await _http.GetAsync(assertionUrl);
        if (respAssertion.StatusCode != HttpStatusCode.Found)
        {
            string b = await respAssertion.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"login/assertion GET did not redirect (status {(int)respAssertion.StatusCode}). Body: {b[..Math.Min(400, b.Length)]}");
        }
        string? folderLoc = respAssertion.Headers.Location?.ToString() ?? "/folder/0";
        string folderUrl = ResolveUrl($"https://{_boxHost}/", folderLoc);

        Log("=== STEP 8: GET the landing folder page to confirm login ===");
        var respFolder = await _http.GetAsync(folderUrl);
        string folderHtml = await respFolder.Content.ReadAsStringAsync();
        Dump("08_folder_landing.html", folderHtml);
        if (!respFolder.IsSuccessStatusCode)
            throw new InvalidOperationException($"Final landing page GET failed (status {(int)respFolder.StatusCode}).");

        string? csrf = GetCsrfTokenFromCookieJar();
        if (string.IsNullOrEmpty(csrf))
            throw new InvalidOperationException("Login flow completed but no csrf-token cookie was set -- login likely did not actually succeed.");

        IsLoggedIn = true;
        Log($"✅ Login complete. csrf-token={csrf[..Math.Min(12, csrf.Length)]}...");
    }

    public string? GetCsrfTokenFromCookieJar()
    {
        var cookies = _handler.CookieContainer.GetCookies(new Uri($"https://{_boxHost}/"));
        foreach (Cookie c in cookies)
            if (c.Name == "csrf-token") return c.Value;
        return null;
    }

    public async Task<List<BoxItem>> GetFolderItemsAsync(string folderId, int limit = 100)
    {
        if (!IsLoggedIn) throw new InvalidOperationException("Not logged in -- call LoginAsync() first.");
        string? csrf = GetCsrfTokenFromCookieJar() ?? throw new InvalidOperationException("No csrf-token cookie available.");

        const string query = @"
    query GetRootFolderMinimal(
        $folderID: ID!
        $offset: NonNegativeInt = 0
        $limit: NonNegativeInt = 20
        $orderBy: MIFolderItemOrder
        $totalCountEstimate: Boolean = true
    ) {
        miFolder(id: $folderID) {
            __typename
            id
            name
            type
            typedId
            fileCount
            rootFolderEntriesV2(
                limit: $limit
                offset: $offset
                orderBy: $orderBy
                totalCountEstimate: $totalCountEstimate
            ) {
                __typename
                hasNext
                totalCountEstimate
                items {
                    __typename
                    ... on CoreMIItem {
                        id
                        name
                        type
                        typedId
                    }
                    ... on MIFile {
                        extension
                        size
                    }
                    ... on MIFolder {
                        fileCount
                        size
                    }
                }
            }
        }
    }";

        var payload = new
        {
            operationName = "GetRootFolderMinimal",
            query,
            variables = new
            {
                folderID = folderId,
                offset = 0,
                limit,
                orderBy = new { field = "DATE", direction = "DESC" },
                totalCountEstimate = true,
            }
        };

        var req = new HttpRequestMessage(HttpMethod.Post, $"https://{_boxHost}/app-api/graphql")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        req.Headers.Referrer = new Uri($"https://{_boxHost}/folder/{folderId}");
        req.Headers.Add("Origin", $"https://{_boxHost}");
        req.Headers.Add("x-csrf-token", csrf);
        req.Headers.Add("request-token", csrf);
        req.Headers.Add("x-request-token", csrf);
        req.Headers.Add("x-box-client-name", "enduserapp");
        req.Headers.Add("x-box-client-version", "23.748.2+hotfix.1");

        var resp = await _http.SendAsync(req);
        string body = await resp.Content.ReadAsStringAsync();
        Dump("graphql_GetRootFolderMinimal.json", body);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"GraphQL folder listing failed (status {(int)resp.StatusCode}). Body: {body[..Math.Min(500, body.Length)]}");

        using var doc = JsonDocument.Parse(body);
        var items = new List<BoxItem>();
        if (doc.RootElement.TryGetProperty("data", out var dataEl) &&
            dataEl.TryGetProperty("miFolder", out var folderEl) &&
            folderEl.ValueKind == JsonValueKind.Object &&
            folderEl.TryGetProperty("rootFolderEntriesV2", out var entriesEl) &&
            entriesEl.TryGetProperty("items", out var itemsEl) &&
            itemsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var it in itemsEl.EnumerateArray())
            {
                items.Add(new BoxItem
                {
                    TypedId = it.TryGetProperty("typedId", out var tid) ? (tid.GetString() ?? "") : "",
                    Id = it.TryGetProperty("id", out var id) ? (id.GetString() ?? "") : "",
                    Type = it.TryGetProperty("type", out var t) ? (t.GetString() ?? "") : "",
                    Name = it.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "",
                    Size = it.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number ? sz.GetInt64() : null,
                    Extension = it.TryGetProperty("extension", out var ext) ? ext.GetString() : null,
                });
            }
        }
        else if (doc.RootElement.TryGetProperty("errors", out var errEl))
        {
            throw new InvalidOperationException("GraphQL folder listing returned errors: " + errEl.ToString());
        }
        return items;
    }

    public async Task<(byte[]? Bytes, string? ContentType, int HttpStatus, string? Error)> DownloadFileAsync(
        string fileId, int timeoutMs = 120_000)
    {
        if (!IsLoggedIn)
            return (null, null, 0, "Not logged in -- call LoginAsync() first.");
        string? csrf = GetCsrfTokenFromCookieJar();
        if (string.IsNullOrEmpty(csrf))
            return (null, null, 0, "No csrf-token cookie available.");

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(Math.Max(1_000, timeoutMs)));

            string downloadUrl = $"https://{_boxHost}/index.php?rm=box_v2_download_file&file_id=f_{fileId}";
            var req = new HttpRequestMessage(HttpMethod.Post, downloadUrl)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["request_token"] = csrf })
            };
            req.Headers.Referrer = new Uri($"https://{_boxHost}/folder/0");
            var resp = await _http.SendAsync(req, cts.Token);

            // The legacy route can either answer directly with the file bytes (200),
            // or redirect to a presigned boxcloud.com URL (302/303) -- handle both.
            if (resp.StatusCode is HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.Redirect)
            {
                string? loc = resp.Headers.Location?.ToString();
                if (string.IsNullOrEmpty(loc))
                    return (null, null, (int)resp.StatusCode, $"Download route {(int)resp.StatusCode}'d with no Location header.");

                var cloudResp = await _http.GetAsync(ResolveUrl(downloadUrl, loc), cts.Token);
                if (!cloudResp.IsSuccessStatusCode)
                    return (null, null, (int)cloudResp.StatusCode, $"Presigned download URL GET failed (status {(int)cloudResp.StatusCode}).");

                byte[] cloudBytes = await cloudResp.Content.ReadAsByteArrayAsync(cts.Token);
                return (cloudBytes, cloudResp.Content.Headers.ContentType?.ToString() ?? "application/octet-stream", (int)cloudResp.StatusCode, null);
            }

            if (!resp.IsSuccessStatusCode)
            {
                string errBody = await resp.Content.ReadAsStringAsync(cts.Token);
                return (null, null, (int)resp.StatusCode,
                    $"HTTP {(int)resp.StatusCode}" + (errBody.Length > 0 ? $": {errBody[..Math.Min(400, errBody.Length)]}" : ""));
            }

            byte[] bytes = await resp.Content.ReadAsByteArrayAsync(cts.Token);
            return (bytes, resp.Content.Headers.ContentType?.ToString() ?? "application/octet-stream", (int)resp.StatusCode, null);
        }
        catch (OperationCanceledException)
        {
            return (null, null, 0, $"Download timed out after {timeoutMs}ms.");
        }
        catch (Exception ex)
        {
            return (null, null, 0, ex.Message);
        }
    }

    public async Task<(byte[]? Bytes, string? ContentType, string? ResolvedFileId, string? Error)> DownloadFileViaSharedLinkAsync(
        string sharedToken, string? knownFileId, int timeoutMs = 120_000)
    {
        string? fileId = string.IsNullOrWhiteSpace(knownFileId) ? null : knownFileId!.Trim();

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(Math.Max(1_000, timeoutMs)));

            string sharedUrl = $"https://{_boxHost}/s/{sharedToken}";
            var respShared = await _http.GetAsync(sharedUrl, cts.Token);
            string html = await respShared.Content.ReadAsStringAsync(cts.Token);
            Dump("shared_link_page.html", html);
            if (!respShared.IsSuccessStatusCode)
                return (null, null, fileId, $"GET shared link page failed (status {(int)respShared.StatusCode}).");

            string? token = null;

            if (fileId != null)
                token = ExtractJsonStringAfter(html, $"\"preview_prefetch_token_map\":{{\"{fileId}\":{{\"read\":\"");

            if (token == null)
            {
                const string mapMarker = "\"preview_prefetch_token_map\":{\"";
                int mapIdx = html.IndexOf(mapMarker, StringComparison.Ordinal);
                if (mapIdx >= 0)
                {
                    int idStart = mapIdx + mapMarker.Length;
                    int idEnd   = html.IndexOf('"', idStart);
                    if (idEnd > idStart)
                    {
                        string candidateId = html.Substring(idStart, idEnd - idStart);
                        const string afterId = "\":{\"read\":\"";
                        if (idEnd + afterId.Length <= html.Length &&
                            string.CompareOrdinal(html.Substring(idEnd, afterId.Length), afterId) == 0)
                        {
                            int tokStart = idEnd + afterId.Length;
                            int tokEnd   = html.IndexOf('"', tokStart);
                            if (tokEnd > tokStart)
                            {
                                fileId ??= candidateId;
                                if (candidateId == fileId)
                                    token = html.Substring(tokStart, tokEnd - tokStart);
                            }
                        }
                    }
                }
            }

            if (fileId == null)
                return (null, null, null,
                    "Could not auto-detect the numeric file ID from the shared-link page " +
                    "(preview_prefetch_token_map not found). Re-call with an explicit numeric " +
                    "file_id together with shared_link_token to skip auto-detection.");

            if (token == null)
                return (null, null, fileId,
                    $"Could not find a preview read-token for file {fileId} on the shared-link page " +
                    "(preview_prefetch_token_map). The shared link may be invalid/expired, or the " +
                    "page has not finished loading.");

            string? downloadUrl = ExtractJsonStringAfter(html, "\"authenticated_download_url\":\"");

            if (downloadUrl == null)
            {
                const string templateMarker = "\"content\":{\"url_template\":\"https:\\/\\/";
                int tIdx = html.IndexOf(templateMarker, StringComparison.Ordinal);
                if (tIdx >= 0)
                {
                    int hostStart = tIdx + templateMarker.Length;
                    int hostEnd   = html.IndexOf(@"\/", hostStart, StringComparison.Ordinal);
                    if (hostEnd > hostStart)
                    {
                        string host = html.Substring(hostStart, hostEnd - hostStart);
                        downloadUrl = $"https://{host}/api/2.0/files/{fileId}/content";
                    }
                }
            }

            if (downloadUrl == null)
                return (null, null, fileId, "Could not resolve the authenticated download URL on the shared-link page.");

            var req = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var resp = await _http.SendAsync(req, cts.Token);

            if (!resp.IsSuccessStatusCode)
            {
                string txt = await resp.Content.ReadAsStringAsync(cts.Token);
                return (null, null, fileId,
                    $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}" + (txt.Length > 0 ? $": {txt[..Math.Min(400, txt.Length)]}" : ""));
            }

            byte[] bytes = await resp.Content.ReadAsByteArrayAsync(cts.Token);
            string ct = resp.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
            return (bytes, ct, fileId, null);
        }
        catch (OperationCanceledException)
        {
            return (null, null, fileId, $"Shared-link download timed out after {timeoutMs}ms.");
        }
        catch (Exception ex)
        {
            return (null, null, fileId, ex.Message);
        }
    }

    private static string? ExtractJsonStringAfter(string html, string marker)
    {
        int idx = html.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return null;
        int start = idx + marker.Length;
        int end = html.IndexOf('"', start);
        if (end < 0) return null;
        return html.Substring(start, end - start).Replace(@"\/", "/");
    }

    public async Task<(int Status, string? Body, string? Error)> ExecuteAsync(
        string method, string url, string? jsonBody, string? bearer, int timeoutMs = 30_000)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(Math.Max(1_000, timeoutMs)));

            var req = new HttpRequestMessage(new HttpMethod(method), url);
            if (!string.IsNullOrEmpty(jsonBody))
                req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            req.Headers.Add("Accept", "application/json");

            if (!string.IsNullOrEmpty(bearer))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            }
            else
            {
                string? csrf = GetCsrfTokenFromCookieJar();
                if (!string.IsNullOrEmpty(csrf))
                {
                    req.Headers.Add("X-Request-Token", csrf);
                    req.Headers.Add("X-CSRF-Token", csrf);
                }
            }

            var resp = await _http.SendAsync(req, cts.Token);
            string body = await resp.Content.ReadAsStringAsync(cts.Token);
            return ((int)resp.StatusCode, body, null);
        }
        catch (OperationCanceledException)
        {
            return (0, null, $"Request timed out after {timeoutMs}ms.");
        }
        catch (Exception ex)
        {
            return (0, null, ex.Message);
        }
    }

    public void Dispose() => _http.Dispose();
}
