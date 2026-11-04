
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace GTA.Tools.Impl.Jira;

public sealed class JiraMyself
{
    public string? AccountId { get; set; }
    public string? EmailAddress { get; set; }
    public string? DisplayName { get; set; }
    public bool Active { get; set; }
    public string RawJson { get; set; } = "";
}

public sealed class JiraApiTokenClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly Action<string>? _log;
    private readonly string _siteUrl;

    public JiraApiTokenClient(string siteUrl, string email, string apiToken, Action<string>? logger = null, bool useSystemProxyWithDefaultCredentials = true)
    {
        _siteUrl = siteUrl.TrimEnd('/');
        _log = logger;

        var handler = new HttpClientHandler
        {
        };
        if (useSystemProxyWithDefaultCredentials)
        {
            handler.UseProxy = true;
            handler.DefaultProxyCredentials = CredentialCache.DefaultCredentials;
        }
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{apiToken}")));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private void Log(string msg) => _log?.Invoke(msg);

    public async Task<JiraMyself> GetMyselfAsync()
    {
        string json = await GetJsonAsync("/rest/api/2/myself");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new JiraMyself
        {
            AccountId = root.TryGetProperty("accountId", out var a) ? a.GetString() : null,
            EmailAddress = root.TryGetProperty("emailAddress", out var e) ? e.GetString() : null,
            DisplayName = root.TryGetProperty("displayName", out var d) ? d.GetString() : null,
            Active = root.TryGetProperty("active", out var ac) && ac.ValueKind == JsonValueKind.True,
            RawJson = json,
        };
    }

    /// <summary>GET /rest/api/2/issue/{issueKey} -- raw JSON (caller parses whatever fields it needs).</summary>
    public Task<string> GetIssueAsync(string issueKey) => GetJsonAsync($"/rest/api/2/issue/{Uri.EscapeDataString(issueKey)}");

    public async Task<string> GetJsonAsync(string path)
    {
        string url = _siteUrl + (path.StartsWith('/') ? path : "/" + path);
        Log($"GET {url}");
        var resp = await _http.GetAsync(url);
        string body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"GET {url} failed (status {(int)resp.StatusCode}): {body}");
        return body;
    }

    public async Task<string> PostJsonAsync(string path, object bodyObject)
    {
        string url = _siteUrl + (path.StartsWith('/') ? path : "/" + path);
        Log($"POST {url}");
        var content = new StringContent(JsonSerializer.Serialize(bodyObject), Encoding.UTF8, "application/json");
        var resp = await _http.PostAsync(url, content);
        string body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"POST {url} failed (status {(int)resp.StatusCode}): {body}");
        return body;
    }

    public async Task<string> PutJsonAsync(string path, object bodyObject)
    {
        string url = _siteUrl + (path.StartsWith('/') ? path : "/" + path);
        Log($"PUT {url}");
        var content = new StringContent(JsonSerializer.Serialize(bodyObject), Encoding.UTF8, "application/json");
        var resp = await _http.PutAsync(url, content);
        string body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"PUT {url} failed (status {(int)resp.StatusCode}): {body}");
        return body;
    }

    public void Dispose() => _http.Dispose();
}
