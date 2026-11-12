
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GTA.Core;
using GTA.Tools.Impl.Common;

namespace GTA.Tools.Impl.Teams;

internal sealed class TeamsSessionState
{
    public string AccessToken { get; set; } = "";
    public string? RefreshToken { get; set; }
    public string? IdToken { get; set; }
    public string? ObjectId { get; set; }
    public string? TenantId { get; set; }
}

/// <summary>
/// Level 0 token cache (Credentials\teams_token_cache.json): the chat token and the service map from the bootstrap.
/// This is a bearer credential for about 1 hour. The file is gitignored (the whole Credentials folder is).
/// </summary>
internal sealed class TeamsTokenCache
{
    public string Email { get; set; } = "";
    public string ChatsvcToken { get; set; } = "";
    public string ChatServiceBaseUrl { get; set; } = "";
    public string MiddleTierBaseUrl { get; set; } = "";
    public string Region { get; set; } = "";
    public string Partition { get; set; } = "";
    public string SkypeToken { get; set; } = "";
    public string? ObjectId { get; set; }
    public string? TenantId { get; set; }
    public DateTime SavedAtUtc { get; set; }
}

/// <summary>Everything learned from the POST /api/authsvc/v1.0/authz bootstrap call.</summary>
public sealed class TeamsRegionInfo
{
    public string Region { get; set; } = "";
    public string Partition { get; set; } = "";
    public string SkypeToken { get; set; } = "";
    public string ChatServiceBaseUrl { get; set; } = "";
    public string MiddleTierBaseUrl { get; set; } = "";
    public string RawAuthzResponse { get; set; } = "";
}

public sealed partial class TeamsSessionClient : IDisposable
{
    private const string ClientId = "5e3ce6c0-2b1f-4285-8d4b-75ee78787346"; // "Microsoft Teams Web Client" MSAL SPA client id
    private const string Scope = "https://api.spaces.skype.com/.default openid profile offline_access";

    private const string ChatSvcScope = "https://ic3.teams.office.com/.default";

    private readonly string _email;
    private readonly string _password;
    private readonly string _sessionFilePath;
    private readonly string _tokenCacheFilePath;
    private readonly Action<string>? _log;
    private readonly IDiagnosticDumper? _dumper;
    private readonly HttpClientHandler _handler;
    private readonly HttpClient _http;

    private string? _accessToken;
    private string? _refreshToken;
    private string? _idToken;
    private string? _objectId;
    private string? _tenantId;
    private TeamsRegionInfo? _region;
    private string? _chatsvcAccessToken;

    public TeamsSessionClient(string email, string password, string? sessionFilePath = null, Action<string>? logger = null,
        IDiagnosticDumper? dumper = null, string? tokenCacheFilePath = null)
    {
        _email = email;
        _password = password;
        _sessionFilePath = sessionFilePath ?? Path.Combine(CredentialManager.CredentialsFolder, "teams_session.json");
        _tokenCacheFilePath = tokenCacheFilePath ?? Path.Combine(CredentialManager.CredentialsFolder, "teams_token_cache.json");
        _log = logger;
        _dumper = dumper;

        _handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true,
            UseProxy = true,
            DefaultProxyCredentials = CredentialCache.DefaultCredentials,
        };
        _http = new HttpClient(_handler) { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Add("Accept-Language", "ja,en-US;q=0.9,en;q=0.8");
    }

    private void Log(string msg) => _log?.Invoke(msg);

    /// <summary>True while the chat token is still valid for more than 1 minute (that is the token every chat call needs).</summary>
    public bool IsLoggedIn => _region != null
        && !string.IsNullOrEmpty(_chatsvcAccessToken)
        && GetJwtExpiryUtc(_chatsvcAccessToken) > DateTime.UtcNow.AddMinutes(1);

    /// <summary>The signed-in user's AAD object id ("oid" claim), once logged in.</summary>
    public string? ObjectId => _objectId;

    /// <summary>The signed-in user's AAD tenant id ("tid" claim), once logged in.</summary>
    public string? TenantId => _tenantId;

    /// <summary>Region/service-map info learned from the last Bootstrap() call.</summary>
    public TeamsRegionInfo? Region => _region;

    public string? ChatSvcAccessToken => _chatsvcAccessToken;

    #region Login / session management

    /// <summary>
    /// Signs in, cheapest way first:
    ///   L0  saved chat token (Credentials\teams_token_cache.json)  -> no login, no bootstrap, no network call at all
    ///   L1  saved AAD session / refresh_token (teams_session.json) -> no password, no ADFS; then the Teams bootstrap
    ///   L2  full ADFS login with the password                     -> only when L0 and L1 both fail
    /// <paramref name="forceFreshLogin"/> = skip L0 and L1 (real ADFS login). <paramref name="skipTokenCache"/> = skip L0 only
    /// (used to renew a token the server just rejected, without touching the password).
    /// </summary>
    public async Task LoginAsync(bool forceFreshLogin = false, bool skipTokenCache = false)
    {
        if (!forceFreshLogin && !skipTokenCache && TryUseTokenCache())
            return;

        if (!forceFreshLogin && TryLoadSessionFromDisk(out var session))
        {
            var expiry = GetJwtExpiryUtc(session!.AccessToken);
            if (expiry > DateTime.UtcNow.AddMinutes(2))
            {
                ApplyAadTokens(session.AccessToken, session.RefreshToken, session.IdToken, session.ObjectId, session.TenantId);
                Log($"Loaded cached AAD session token from disk -- still valid until {expiry:u}. No ADFS login needed.");
            }
            else if (!string.IsNullOrEmpty(session.RefreshToken))
            {
                try
                {
                    Log("Cached AAD access_token is expired/near-expiry -- trying refresh_token exchange (no password needed)...");
                    await RefreshAccessTokenAsync(session.RefreshToken!);
                    Log("Refresh succeeded -- AAD session renewed without a full login.");
                }
                catch (Exception ex)
                {
                    Log($"Refresh token exchange failed ({ex.Message}); falling back to a full ADFS login.");
                    await FullAdfsLoginAsync();
                }
            }
            else
            {
                await FullAdfsLoginAsync();
            }
        }
        else
        {
            await FullAdfsLoginAsync();
        }

        await BootstrapAsync();
    }

    private async Task FullAdfsLoginAsync()
    {
        Log("Performing full ADFS/WS-Federation/KMSI login (email+password)...");
        using var adfsClient = new TeamsAdfsLoginClient(logger: _log, dumper: _dumper);
        var tokens = await adfsClient.LoginAsync(_email, _password);
        ApplyAadTokens(tokens.AccessToken, tokens.RefreshToken, tokens.IdToken, tokens.ObjectId, tokens.TenantId);
        SaveSessionToDisk();
    }

    private async Task RefreshAccessTokenAsync(string refreshToken)
    {
        var fields = new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["scope"] = Scope,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        };
        var resp = await _http.PostAsync(
            "https://login.microsoftonline.com/common/oauth2/v2.0/token",
            new FormUrlEncodedContent(fields));
        string body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"refresh_token exchange failed (status {(int)resp.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        string newAccessToken = root.GetProperty("access_token").GetString()!;
        // AAD does not always issue a brand new refresh_token on every refresh -- keep the old one if a new one wasn't supplied.
        string? newRefreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : refreshToken;
        string? newIdToken = root.TryGetProperty("id_token", out var it) ? it.GetString() : null;
        ApplyAadTokens(newAccessToken, newRefreshToken, newIdToken,
            TryGetJwtClaim(newAccessToken, "oid"), TryGetJwtClaim(newAccessToken, "tid"));
        SaveSessionToDisk();
    }

    private void ApplyAadTokens(string accessToken, string? refreshToken, string? idToken, string? objectId, string? tenantId)
    {
        _accessToken = accessToken;
        _refreshToken = refreshToken;
        _idToken = idToken;
        _objectId = objectId;
        _tenantId = tenantId;
    }

    private async Task BootstrapAsync()
    {
        EnsureAadLoggedIn();

        Log("=== Teams bootstrap STEP A: POST /api/authsvc/v1.0/authz (AAD bearer -> skypeToken + region + service map) ===");
        var reqAuthz = new HttpRequestMessage(HttpMethod.Post, "https://teams.microsoft.com/api/authsvc/v1.0/authz");
        reqAuthz.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        reqAuthz.Headers.Add("ms-teams-authz-type", "ExplicitLogin");
        reqAuthz.Headers.Add("x-ms-client-type", "web");
        reqAuthz.Headers.Add("Origin", "https://teams.microsoft.com");
        reqAuthz.Headers.Referrer = new Uri($"https://teams.microsoft.com/v2/?tenantId={_tenantId}");
        reqAuthz.Content = new ByteArrayContent(Array.Empty<byte>());
        reqAuthz.Content.Headers.ContentLength = 0;
        var respAuthz = await _http.SendAsync(reqAuthz);
        string bodyAuthz = await respAuthz.Content.ReadAsStringAsync();
        _dumper?.Dump("teams_authz.json", bodyAuthz);
        if (!respAuthz.IsSuccessStatusCode)
            throw new InvalidOperationException($"POST /api/authsvc/v1.0/authz failed (status {(int)respAuthz.StatusCode}). Body: {bodyAuthz}");

        using var doc = JsonDocument.Parse(bodyAuthz);
        var root = doc.RootElement;
        string region = root.TryGetProperty("region", out var regionEl) ? regionEl.GetString() ?? "" : "";
        string partition = root.TryGetProperty("partition", out var partitionEl) ? partitionEl.GetString() ?? "" : "";
        string skypeToken = root.TryGetProperty("tokens", out var tokensEl) && tokensEl.TryGetProperty("skypeToken", out var skEl)
            ? skEl.GetString() ?? "" : "";
        string chatServiceBaseUrl = "", middleTierBaseUrl = "";
        if (root.TryGetProperty("regionGtms", out var gtmsEl))
        {
            if (gtmsEl.TryGetProperty("chatServiceAfd", out var csEl)) chatServiceBaseUrl = csEl.GetString() ?? "";
            if (gtmsEl.TryGetProperty("middleTier", out var mtEl)) middleTierBaseUrl = mtEl.GetString() ?? "";
        }
        if (string.IsNullOrEmpty(skypeToken))
            throw new InvalidOperationException($"authz response did not contain tokens.skypeToken. Body: {bodyAuthz[..Math.Min(500, bodyAuthz.Length)]}");
        _region = new TeamsRegionInfo
        {
            Region = region,
            Partition = partition,
            SkypeToken = skypeToken,
            ChatServiceBaseUrl = chatServiceBaseUrl,
            MiddleTierBaseUrl = middleTierBaseUrl,
            RawAuthzResponse = bodyAuthz,
        };
        Log($"    region={region} partition={partition} chatServiceBaseUrl={chatServiceBaseUrl} middleTierBaseUrl={middleTierBaseUrl}");

        Log("=== Teams bootstrap STEP B: POST /api/mt/{region}/beta/users/{oid}/cookiev2 (-> sets the \"authtoken\" session cookie) ===");
        if (string.IsNullOrEmpty(_objectId))
            throw new InvalidOperationException("Cannot call cookiev2 -- no AAD object id (oid) known for the signed-in user.");
        string cookieV2Url = $"{(string.IsNullOrEmpty(middleTierBaseUrl) ? $"https://teams.microsoft.com/api/mt/{region}" : middleTierBaseUrl)}/beta/users/{_objectId}/cookiev2";
        var reqCookie = new HttpRequestMessage(HttpMethod.Post, cookieV2Url);
        reqCookie.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        reqCookie.Headers.Add("x-ms-client-caller", "imageAuthCookieSetupEM"); // matches the real captured header value verbatim
        reqCookie.Headers.Add("x-ms-client-type", "web");
        reqCookie.Headers.Add("Origin", "https://teams.microsoft.com");
        reqCookie.Headers.Referrer = new Uri($"https://teams.microsoft.com/v2/?tenantId={_tenantId}");
        reqCookie.Content = new ByteArrayContent(Array.Empty<byte>());
        reqCookie.Content.Headers.ContentLength = 0;
        var respCookie = await _http.SendAsync(reqCookie);
        if (!respCookie.IsSuccessStatusCode)
        {
            string errBody = await respCookie.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"POST cookiev2 failed (status {(int)respCookie.StatusCode}). Body: {errBody}");
        }
        bool gotAuthTokenCookie = _handler.CookieContainer
            .GetCookies(new Uri("https://teams.microsoft.com/"))
            .Cast<Cookie>()
            .Any(c => c.Name == "authtoken");
        Log($"    cookiev2 call succeeded. \"authtoken\" cookie present in cookie jar: {gotAuthTokenCookie}. THIS is the Teams session cookie.");

        Log("=== Teams bootstrap STEP C (best-effort, non-fatal): POST skypetokenauth on asm.skype.com / asyncgw -- sets skypetoken_asm cookie for calling messaging/presence backends directly ===");
        foreach (var skypeTokenAuthUrl in new[]
        {
            "https://as-api.asm.skype.com/v1/skypetokenauth",
            "https://as-prod.asyncgw.teams.microsoft.com/v1/skypetokenauth",
        })
        {
            try
            {
                var reqSk = new HttpRequestMessage(HttpMethod.Post, skypeTokenAuthUrl)
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["skypetoken"] = skypeToken }),
                };
                reqSk.Headers.Add("Authorization", $"skype_token {skypeToken}");
                reqSk.Headers.Add("Origin", "https://teams.microsoft.com");
                reqSk.Headers.Referrer = new Uri("https://teams.microsoft.com/");
                var respSk = await _http.SendAsync(reqSk);
                Log($"    {skypeTokenAuthUrl} -> status {(int)respSk.StatusCode}");
            }
            catch (Exception ex)
            {
                Log($"    {skypeTokenAuthUrl} -> best-effort call failed, ignoring ({ex.Message})");
            }
        }

        Log("=== Teams bootstrap STEP D: silent refresh_token grant for the chatsvc (ic3.teams.office.com) resource ===");
        if (string.IsNullOrEmpty(_refreshToken))
        {
            Log("    No refresh_token available -- chatsvc calls will not work (falling back to the api.spaces.skype.com token, which chatsvc does NOT accept).");
            _chatsvcAccessToken = _accessToken;
        }
        else
        {
            try
            {
                var chatFields = new Dictionary<string, string>
                {
                    ["client_id"] = ClientId,
                    ["scope"] = ChatSvcScope,
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = _refreshToken!,
                };
                var reqChatTok = new HttpRequestMessage(HttpMethod.Post, "https://login.microsoftonline.com/common/oauth2/v2.0/token")
                { Content = new FormUrlEncodedContent(chatFields) };
                reqChatTok.Headers.Referrer = new Uri("https://teams.microsoft.com/");
                reqChatTok.Headers.Add("Origin", "https://teams.microsoft.com");
                var respChatTok = await _http.SendAsync(reqChatTok);
                string bodyChatTok = await respChatTok.Content.ReadAsStringAsync();
                _dumper?.Dump("teams_chatsvc_token.json", bodyChatTok);
                if (!respChatTok.IsSuccessStatusCode)
                    throw new InvalidOperationException($"chatsvc-scoped refresh_token grant failed (status {(int)respChatTok.StatusCode}). Body: {bodyChatTok}");
                using var chatTokDoc = JsonDocument.Parse(bodyChatTok);
                _chatsvcAccessToken = chatTokDoc.RootElement.GetProperty("access_token").GetString()!;
                Log($"    Acquired chatsvc-scoped access_token (aud=https://ic3.teams.office.com).");
            }
            catch (Exception ex)
            {
                Log($"    Failed to acquire chatsvc-scoped token ({ex.Message}) -- falling back to the api.spaces.skype.com token, which chatsvc will likely reject.");
                _chatsvcAccessToken = _accessToken;
            }
        }

        // Level 0 cache: the next client can skip login + bootstrap completely.
        SaveTokenCache();
    }

    #region Token cache (level 0): skip login AND bootstrap

    /// <summary>
    /// Saves the chat token + service map, so the NEXT client (next tool call, or next program start) can use them
    /// without any login and without the bootstrap calls. Written to a temp file first, then moved, so a second
    /// process never reads a half-written file.
    /// </summary>
    private void SaveTokenCache()
    {
        try
        {
            if (_region == null || string.IsNullOrEmpty(_chatsvcAccessToken)) return;
            // The fallback (chat token = AAD token) is rejected by the chat service. Never cache that.
            if (string.Equals(_chatsvcAccessToken, _accessToken, StringComparison.Ordinal)) return;

            var cache = new TeamsTokenCache
            {
                Email = _email,
                ChatsvcToken = _chatsvcAccessToken!,
                ChatServiceBaseUrl = _region.ChatServiceBaseUrl,
                MiddleTierBaseUrl = _region.MiddleTierBaseUrl,
                Region = _region.Region,
                Partition = _region.Partition,
                SkypeToken = _region.SkypeToken,
                ObjectId = _objectId,
                TenantId = _tenantId,
                SavedAtUtc = DateTime.UtcNow,
            };
            string? dir = Path.GetDirectoryName(_tokenCacheFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = _tokenCacheFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(cache));
            File.Move(tmp, _tokenCacheFilePath, overwrite: true);
            Log($"Teams token cache saved (chat token valid until {GetJwtExpiryUtc(_chatsvcAccessToken!):u}): {_tokenCacheFilePath}");
        }
        catch (Exception ex)
        {
            Log($"Could not save the Teams token cache ({ex.Message}) -- continuing without it.");
        }
    }

    /// <summary>
    /// Level 0. Uses the saved chat token when it is still valid for more than 2 minutes: no ADFS login, no AAD call,
    /// no bootstrap. The AAD refresh_token is still loaded from the session file, because the Graph tools need it.
    /// Returns false (nothing changed) when the cache cannot be used.
    /// </summary>
    private bool TryUseTokenCache()
    {
        try
        {
            if (!File.Exists(_tokenCacheFilePath)) { Log("Token cache: no file yet."); return false; }
            var cache = JsonSerializer.Deserialize<TeamsTokenCache>(File.ReadAllText(_tokenCacheFilePath));
            if (cache == null || string.IsNullOrEmpty(cache.ChatsvcToken) || string.IsNullOrEmpty(cache.ChatServiceBaseUrl) || string.IsNullOrEmpty(cache.SkypeToken))
            { Log("Token cache: file is incomplete, ignoring it."); return false; }
            if (!string.Equals(cache.Email, _email, StringComparison.OrdinalIgnoreCase))
            { Log("Token cache: it belongs to another account, ignoring it."); return false; }

            DateTime expiry = GetJwtExpiryUtc(cache.ChatsvcToken);
            if (expiry <= DateTime.UtcNow.AddMinutes(2))
            { Log($"Token cache: chat token is expired or nearly expired ({expiry:u})."); return false; }

            if (!TryLoadSessionFromDisk(out var session) || session == null || string.IsNullOrEmpty(session.RefreshToken))
            { Log("Token cache: no refresh_token in the session file (Graph tools would fail), ignoring the cache."); return false; }

            ApplyAadTokens(session.AccessToken, session.RefreshToken, session.IdToken,
                session.ObjectId ?? cache.ObjectId, session.TenantId ?? cache.TenantId);
            _region = new TeamsRegionInfo
            {
                Region = cache.Region,
                Partition = cache.Partition,
                SkypeToken = cache.SkypeToken,
                ChatServiceBaseUrl = cache.ChatServiceBaseUrl,
                MiddleTierBaseUrl = cache.MiddleTierBaseUrl,
                RawAuthzResponse = "",
            };
            _chatsvcAccessToken = cache.ChatsvcToken;
            Log($"Token cache HIT: reusing the saved chat token (valid until {expiry:u}). No login, no bootstrap.");
            return true;
        }
        catch (Exception ex)
        {
            Log($"Token cache: could not be read ({ex.Message}), ignoring it.");
            return false;
        }
    }

    #endregion

    private void EnsureAadLoggedIn()
    {
        if (string.IsNullOrEmpty(_accessToken))
            throw new InvalidOperationException("Not logged in -- call LoginAsync() first.");
    }

    private void EnsureBootstrapped()
    {
        EnsureAadLoggedIn();
        if (_region == null)
            throw new InvalidOperationException("Teams bootstrap has not run -- call LoginAsync() first.");
    }

    private bool TryLoadSessionFromDisk(out TeamsSessionState? session)
    {
        session = null;
        try
        {
            if (!File.Exists(_sessionFilePath)) return false;
            string json = File.ReadAllText(_sessionFilePath);
            session = JsonSerializer.Deserialize<TeamsSessionState>(json);
            return session != null && !string.IsNullOrEmpty(session.AccessToken);
        }
        catch (Exception ex)
        {
            Log($"Could not read cached session file ({ex.Message}); ignoring cache.");
            return false;
        }
    }

    private void SaveSessionToDisk()
    {
        try
        {
            var session = new TeamsSessionState
            {
                AccessToken = _accessToken!,
                RefreshToken = _refreshToken,
                IdToken = _idToken,
                ObjectId = _objectId,
                TenantId = _tenantId,
            };
            var dir = Path.GetDirectoryName(_sessionFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_sessionFilePath, JsonSerializer.Serialize(session));
            Log($"AAD session token cached to: {_sessionFilePath}");
        }
        catch (Exception ex)
        {
            Log($"Could not persist session token to disk ({ex.Message}) -- continuing in-memory only.");
        }
    }

    /// <summary>Deletes both cache files (AAD session + chat token cache). The next login is a full ADFS login.</summary>
    public void ClearCachedSession()
    {
        try { if (File.Exists(_sessionFilePath)) File.Delete(_sessionFilePath); } catch { /* best effort */ }
        try { if (File.Exists(_tokenCacheFilePath)) File.Delete(_tokenCacheFilePath); } catch { /* best effort */ }
    }

    private static DateTime GetJwtExpiryUtc(string jwt)
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
        catch
        {
            return DateTime.MinValue;
        }
    }

    private static string? TryGetJwtClaim(string jwt, string claimName)
    {
        try
        {
            string[] parts = jwt.Split('.');
            if (parts.Length < 2) return null;
            string payload = parts[1].Replace('-', '+').Replace('_', '/');
            switch (payload.Length % 4) { case 2: payload += "=="; break; case 3: payload += "="; break; }
            byte[] bytes = Convert.FromBase64String(payload);
            using var doc = JsonDocument.Parse(bytes);
            return doc.RootElement.TryGetProperty(claimName, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;
        }
        catch { return null; }
    }

    #endregion

    #region REST demo calls (all proven directly against the captured traffic, no pre-known ids required)

    public async Task<string> GetTenantsAsync()
    {
        EnsureBootstrapped();
        string url = $"{_region!.MiddleTierBaseUrl}/beta/users/tenantsv2?invalidate={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        return await GetTeamsApiAsync(url);
    }

    public async Task<string> GetChatServicePropertiesAsync()
    {
        EnsureBootstrapped();
        string url = $"{_region!.ChatServiceBaseUrl}/v1/users/ME/properties";
        return await GetTeamsApiAsync(url, bearerTokenOverride: _chatsvcAccessToken);
    }

    public async Task<string> GetThreadRosterAsync(int pageSize = 200)
    {
        EnsureBootstrapped();
        string url = $"{_region!.ChatServiceBaseUrl}/v1/users/ME/conversations/48%3Athreads/messages" +
                     $"?view=msnp24Equivalent%7CsupportsMessageProperties&pageSize={pageSize}&startTime=1";
        return await GetTeamsApiAsync(url, bearerTokenOverride: _chatsvcAccessToken);
    }
    public async Task<string> GetSelfChatMessagesAsync(int pageSize = 50)
    {
        EnsureBootstrapped();
        string url = $"{_region!.ChatServiceBaseUrl}/v1/users/ME/conversations/48%3Anotes/messages" +
                     $"?view=msnp24Equivalent%7CsupportsMessageProperties&pageSize={pageSize}&startTime=1";
        return await GetTeamsApiAsync(url, bearerTokenOverride: _chatsvcAccessToken);
    }
    public async Task<string> SendSelfChatMessageAsync(string text)
    {
        EnsureBootstrapped();
        string url = $"{_region!.ChatServiceBaseUrl}/v1/users/ME/conversations/48%3Anotes/messages";
        string clientMessageId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        var body = new
        {
            content = WebUtility.HtmlEncode(text),
            messagetype = "Text",
            contenttype = "text",
            amsreferences = Array.Empty<string>(),
            clientmessageid = clientMessageId,
        };
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _chatsvcAccessToken);
        req.Headers.Add("Accept", "application/json");
        req.Headers.Add("x-ms-client-type", "web");
        req.Headers.Referrer = new Uri($"https://teams.microsoft.com/v2/?tenantId={_tenantId}");
        var resp = await _http.SendAsync(req);
        string respBody = await resp.Content.ReadAsStringAsync();
        _dumper?.Dump("teams_send_self_chat.json", $"REQUEST:\n{JsonSerializer.Serialize(body)}\n\nRESPONSE (status {(int)resp.StatusCode}):\n{respBody}");
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"POST {url} failed (status {(int)resp.StatusCode}). Body: {respBody}");
        return respBody;
    }

    public async Task<string> GetTeamsApiAsync(string url, string? bearerTokenOverride = null)
    {
        EnsureBootstrapped();
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerTokenOverride ?? _accessToken);
        req.Headers.Add("Accept", "application/json");
        req.Headers.Add("x-ms-client-type", "web");
        req.Headers.Referrer = new Uri($"https://teams.microsoft.com/v2/?tenantId={_tenantId}");
        var resp = await _http.SendAsync(req);
        string body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"GET {url} failed (status {(int)resp.StatusCode}). Body: {body}");
        return body;
    }

    #endregion

    public void Dispose() => _http.Dispose();
}
