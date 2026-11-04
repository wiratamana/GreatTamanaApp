
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Text.Unicode;
using GTA.Core;
using GTA.Tools.Impl.Common;

namespace GTA.Tools.Impl.Outlook;

#region DTOs

/// <summary>A sanitized (Name, Address) pair -- mirrors Outlook REST API's "EmailAddress" shape, flattened.</summary>
public sealed class EmailAddressInfo
{
    public string? Name { get; set; }
    public string? Address { get; set; }
    public override string ToString() => string.IsNullOrEmpty(Name) ? (Address ?? "") : $"{Name} <{Address}>";
}

public sealed class EmailAttachmentInfo
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? ContentType { get; set; }
    public long SizeBytes { get; set; }
    public bool IsInline { get; set; }
}

/// <summary>A single inbox message, sanitized down from the raw, very verbose Outlook REST API v2.0 JSON.</summary>
public sealed class EmailMessage
{
    public string? Id { get; set; }
    public string? Subject { get; set; }
    public EmailAddressInfo? From { get; set; }
    public EmailAddressInfo? Sender { get; set; }
    public List<EmailAddressInfo> To { get; set; } = new();
    public List<EmailAddressInfo> Cc { get; set; } = new();
    public DateTimeOffset? ReceivedDateTime { get; set; }
    public DateTimeOffset? SentDateTime { get; set; }
    public bool IsRead { get; set; }
    public bool HasAttachments { get; set; }
    /// <summary>Short plain-text preview, as supplied directly by the API.</summary>
    public string? BodyPreview { get; set; }
    public string? Body { get; set; }
    public string? WebLink { get; set; }
    public List<EmailAttachmentInfo> Attachments { get; set; } = new();
}

public sealed class InboxQuery
{
    public string? SearchText { get; set; }

    public DateTimeOffset? ReceivedAfter { get; set; }
    public DateTimeOffset? ReceivedBefore { get; set; }
    public string? FromAddress { get; set; }
    public string? CcAddress { get; set; }
    public bool? UnreadOnly { get; set; }
    public bool? HasAttachmentsOnly { get; set; }

    /// <summary>Max messages to return. Defaults to 10 ("no param" case).</summary>
    public int Top { get; set; } = 10;

    public bool IncludeAttachmentsMetadata { get; set; } = true;
}

#endregion

internal sealed class OutlookSessionState
{
    public string AccessToken { get; set; } = "";
    public string? RefreshToken { get; set; }
    public string? IdToken { get; set; }
}

public sealed class OutlookMailboxClient : IDisposable
{
    private const string ClientId = "9199bf20-a13f-4107-85dc-02114787ef48"; // "Outlook Web" MSAL SPA client id
    private const string Scope = "https://outlook.office.com/.default openid profile offline_access";
    private const string RestBase = "https://outlook.office.com/api/v2.0/me";

    private readonly string _email;
    private readonly string _password;
    private readonly string _sessionFilePath;
    private readonly Action<string>? _log;
    private readonly IDiagnosticDumper? _dumper;
    private readonly HttpClient _http;

    private string? _accessToken;
    private string? _refreshToken;
    private string? _idToken;

    public OutlookMailboxClient(string email, string password, string? sessionFilePath = null, Action<string>? logger = null, IDiagnosticDumper? dumper = null)
    {
        _email = email;
        _password = password;
        _sessionFilePath = sessionFilePath ?? Path.Combine(CredentialManager.CredentialsFolder, "outlook_session.json");
        _log = logger;
        _dumper = dumper;

        var handler = new HttpClientHandler
        {
            UseProxy = true,
            DefaultProxyCredentials = CredentialCache.DefaultCredentials,
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    private void Log(string msg) => _log?.Invoke(msg);

    /// <summary>True once a (still valid, per its own JWT expiry) access_token is held in memory.</summary>
    public bool IsLoggedIn => !string.IsNullOrEmpty(_accessToken) && GetJwtExpiryUtc(_accessToken) > DateTime.UtcNow;

    #region Login / session management

    public async Task LoginAsync(bool forceFreshLogin = false)
    {
        if (!forceFreshLogin && TryLoadSessionFromDisk(out var session))
        {
            var expiry = GetJwtExpiryUtc(session!.AccessToken);
            if (expiry > DateTime.UtcNow.AddMinutes(2))
            {
                _accessToken = session.AccessToken;
                _refreshToken = session.RefreshToken;
                _idToken = session.IdToken;
                Log($"Loaded cached session token from disk -- still valid until {expiry:u}. No network call needed.");
                return;
            }

            if (!string.IsNullOrEmpty(session.RefreshToken))
            {
                try
                {
                    Log("Cached access_token is expired/near-expiry -- trying refresh_token exchange (no password needed)...");
                    await RefreshAccessTokenAsync(session.RefreshToken!);
                    Log("Refresh succeeded -- session renewed without a full login.");
                    return;
                }
                catch (Exception ex)
                {
                    Log($"Refresh token exchange failed ({ex.Message}); falling back to a full ADFS login.");
                }
            }
        }

        await FullAdfsLoginAsync();
    }

    private async Task FullAdfsLoginAsync()
    {
        Log("Performing full ADFS/WS-Federation/KMSI login (email+password)...");
        using var adfsClient = new OutlookAdfsLoginClient(logger: _log, dumper: _dumper);
        var tokens = await adfsClient.LoginAsync(_email, _password);
        ApplyTokens(tokens.AccessToken, tokens.RefreshToken, tokens.IdToken);
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
        ApplyTokens(newAccessToken, newRefreshToken, newIdToken);
        SaveSessionToDisk();
    }

    private void ApplyTokens(string accessToken, string? refreshToken, string? idToken)
    {
        _accessToken = accessToken;
        _refreshToken = refreshToken;
        _idToken = idToken;
    }

    private void EnsureLoggedIn()
    {
        if (string.IsNullOrEmpty(_accessToken))
            throw new InvalidOperationException("Not logged in -- call LoginAsync() first.");
    }

    private bool TryLoadSessionFromDisk(out OutlookSessionState? session)
    {
        session = null;
        try
        {
            if (!File.Exists(_sessionFilePath)) return false;
            string json = File.ReadAllText(_sessionFilePath);
            session = JsonSerializer.Deserialize<OutlookSessionState>(json);
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
            var session = new OutlookSessionState { AccessToken = _accessToken!, RefreshToken = _refreshToken, IdToken = _idToken };
            var dir = Path.GetDirectoryName(_sessionFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_sessionFilePath, JsonSerializer.Serialize(session));
            Log($"Session token cached to: {_sessionFilePath}");
        }
        catch (Exception ex)
        {
            Log($"Could not persist session token to disk ({ex.Message}) -- continuing in-memory only.");
        }
    }

    public void ClearCachedSession()
    {
        try { if (File.Exists(_sessionFilePath)) File.Delete(_sessionFilePath); } catch { /* best effort */ }
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

    #endregion

    #region Inbox fetching

    public async Task<List<EmailMessage>> FetchInboxAsync(InboxQuery? query = null)
    {
        query ??= new InboxQuery();
        EnsureLoggedIn();

        string odataQuery = BuildODataQuery(query);
        string url = $"{RestBase}/MailFolders/Inbox/Messages?{odataQuery}";
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        req.Headers.Add("Accept", "application/json");
        var resp = await _http.SendAsync(req);
        string body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Inbox fetch failed (status {(int)resp.StatusCode}): {body}");

        return ParseMessages(body);
    }

    /// <summary>No-parameter case: the 10 (or `top`) most recent inbox messages.</summary>
    public Task<List<EmailMessage>> FetchLatestAsync(int top = 10) =>
        FetchInboxAsync(new InboxQuery { Top = top });

    /// <summary>Free-text search across subject/body/sender.</summary>
    public Task<List<EmailMessage>> SearchAsync(string searchText, int top = 25) =>
        FetchInboxAsync(new InboxQuery { SearchText = searchText, Top = top });

    /// <summary>All messages received within [from, to].</summary>
    public Task<List<EmailMessage>> FetchByDateRangeAsync(DateTimeOffset from, DateTimeOffset to, int top = 50) =>
        FetchInboxAsync(new InboxQuery { ReceivedAfter = from, ReceivedBefore = to, Top = top });

    /// <summary>Messages from a given sender and/or with a given address on the Cc line (pass either or both).</summary>
    public Task<List<EmailMessage>> FetchByFromOrCcAsync(string? from = null, string? cc = null, int top = 50) =>
        FetchInboxAsync(new InboxQuery { FromAddress = from, CcAddress = cc, Top = top });

    public async Task<byte[]> DownloadAttachmentAsync(string messageId, string attachmentId)
    {
        EnsureLoggedIn();
        string url = $"{RestBase}/Messages('{Uri.EscapeDataString(messageId)}')/Attachments('{Uri.EscapeDataString(attachmentId)}')/$value";
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        var resp = await _http.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Attachment download failed (status {(int)resp.StatusCode}).");
        return await resp.Content.ReadAsByteArrayAsync();
    }

    private static string BuildODataQuery(InboxQuery q)
    {
        var parts = new List<string> { $"$top={Math.Max(1, q.Top)}" };

        bool hasSearch = !string.IsNullOrWhiteSpace(q.SearchText);
        if (hasSearch)
        {
            // Outlook REST API v2.0 does not support combining $search with $filter/$orderby -- $search alone.
            parts.Add("$search=" + Uri.EscapeDataString($"\"{q.SearchText}\""));
        }
        else
        {
            var filters = new List<string>();
            if (q.ReceivedAfter.HasValue) filters.Add($"ReceivedDateTime ge {q.ReceivedAfter.Value.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}");
            if (q.ReceivedBefore.HasValue) filters.Add($"ReceivedDateTime le {q.ReceivedBefore.Value.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}");
            bool hasAddressFilter = !string.IsNullOrWhiteSpace(q.FromAddress) || !string.IsNullOrWhiteSpace(q.CcAddress);
            if (!string.IsNullOrWhiteSpace(q.FromAddress)) filters.Add($"From/EmailAddress/Address eq '{q.FromAddress}'");
            if (!string.IsNullOrWhiteSpace(q.CcAddress)) filters.Add($"CcRecipients/any(c:c/EmailAddress/Address eq '{q.CcAddress}')");
            if (q.UnreadOnly == true) filters.Add("IsRead eq false");
            if (q.HasAttachmentsOnly == true) filters.Add("HasAttachments eq true");
            if (filters.Count > 0)
                parts.Add("$filter=" + Uri.EscapeDataString(string.Join(" and ", filters)));

            if (!hasAddressFilter)
                parts.Add("$orderby=" + Uri.EscapeDataString("ReceivedDateTime desc"));
        }

        if (q.IncludeAttachmentsMetadata)
            parts.Add("$expand=" + Uri.EscapeDataString("Attachments($select=Id,Name,ContentType,Size,IsInline)"));

        return string.Join("&", parts);
    }

    #endregion

    #region Sanitizing / parsing

    public static List<EmailMessage> ParseMessages(string rawJson)
    {
        var result = new List<EmailMessage>();
        using var doc = JsonDocument.Parse(rawJson);
        if (!doc.RootElement.TryGetProperty("value", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var m in arr.EnumerateArray())
            result.Add(ParseOneMessage(m));
        return result;
    }

    private static EmailMessage ParseOneMessage(JsonElement m)
    {
        var msg = new EmailMessage
        {
            Id = GetStr(m, "Id"),
            Subject = GetStr(m, "Subject"),
            From = m.TryGetProperty("From", out var fromEl) ? ParseAddress(fromEl) : null,
            Sender = m.TryGetProperty("Sender", out var senderEl) ? ParseAddress(senderEl) : null,
            To = m.TryGetProperty("ToRecipients", out var toEl) ? ParseAddressList(toEl) : new(),
            Cc = m.TryGetProperty("CcRecipients", out var ccEl) ? ParseAddressList(ccEl) : new(),
            ReceivedDateTime = TryGetDate(m, "ReceivedDateTime"),
            SentDateTime = TryGetDate(m, "SentDateTime"),
            IsRead = m.TryGetProperty("IsRead", out var irEl) && irEl.ValueKind == JsonValueKind.True,
            HasAttachments = m.TryGetProperty("HasAttachments", out var haEl) && haEl.ValueKind == JsonValueKind.True,
            BodyPreview = GetStr(m, "BodyPreview"),
            WebLink = GetStr(m, "WebLink"),
        };

        if (m.TryGetProperty("Body", out var bodyEl) && bodyEl.ValueKind == JsonValueKind.Object)
        {
            string? content = bodyEl.TryGetProperty("Content", out var c) ? c.GetString() : null;
            string? contentType = bodyEl.TryGetProperty("ContentType", out var ct) ? ct.GetString() : null;
            msg.Body = string.Equals(contentType, "HTML", StringComparison.OrdinalIgnoreCase)
                ? HtmlToPlainText(content)
                : content;
        }

        if (m.TryGetProperty("Attachments", out var attEl) && attEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in attEl.EnumerateArray())
            {
                msg.Attachments.Add(new EmailAttachmentInfo
                {
                    Id = GetStr(a, "Id"),
                    Name = GetStr(a, "Name"),
                    ContentType = GetStr(a, "ContentType"),
                    SizeBytes = a.TryGetProperty("Size", out var sz) && sz.ValueKind == JsonValueKind.Number ? sz.GetInt64() : 0,
                    IsInline = a.TryGetProperty("IsInline", out var ii) && ii.ValueKind == JsonValueKind.True,
                });
            }
        }

        return msg;
    }

    private static EmailAddressInfo? ParseAddress(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty("EmailAddress", out var addr) || addr.ValueKind != JsonValueKind.Object)
            return null;
        return new EmailAddressInfo
        {
            Name = GetStr(addr, "Name"),
            Address = GetStr(addr, "Address"),
        };
    }

    private static List<EmailAddressInfo> ParseAddressList(JsonElement arr)
    {
        var list = new List<EmailAddressInfo>();
        if (arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var item in arr.EnumerateArray())
        {
            var addr = ParseAddress(item);
            if (addr != null) list.Add(addr);
        }
        return list;
    }

    private static string? GetStr(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTimeOffset? TryGetDate(JsonElement el, string name)
    {
        string? s = GetStr(el, name);
        return s != null && DateTimeOffset.TryParse(s, out var dt) ? dt : null;
    }

    private static string? HtmlToPlainText(string? html)
    {
        if (string.IsNullOrEmpty(html)) return html;
        string text = Regex.Replace(html, @"<script[^>]*>.*?</script>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<style[^>]*>.*?</style>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<(br|/p|/div|/tr|/li|/h[1-6])\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", "");
        text = WebUtility.HtmlDecode(text);
        text = text.Replace("\u200c", "").Replace("\u00a0", " ");
        text = Regex.Replace(text, @"[ \t]{2,}", " ");
        text = Regex.Replace(text, @"\n[ \t]*\n[ \t]*(\n[ \t]*)+", "\n\n");
        return text.Trim();
    }

    #endregion

    public static readonly JsonSerializerOptions PrettyJsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    /// <summary>Convenience: serialize a batch of messages to clean, indented JSON.</summary>
    public static string ToJson(IEnumerable<EmailMessage> messages) =>
        JsonSerializer.Serialize(messages, PrettyJsonOptions);

    public void Dispose() => _http.Dispose();
}
