
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace GTA.Tools.Impl.Teams;

// Microsoft Graph part of the client: Graph token, user lookup, chat list, OneDrive upload.
// The Graph token comes from the SAME sign-in (same refresh_token and client id, other scope) -- no extra login.
public sealed partial class TeamsSessionClient
{
    private const string GraphScope = "https://graph.microsoft.com/.default";
    private const string GraphBase = "https://graph.microsoft.com/v1.0";

    /// <summary>OneDrive folder that Teams itself uses for files sent in chats.</summary>
    private const string UploadFolder = "Microsoft Teams Chat Files";

    /// <summary>Files up to this size go in ONE request. Bigger files use an upload session (chunks).</summary>
    private const int SimpleUploadMaxBytes = 4 * 1024 * 1024;

    /// <summary>Chunk size of an upload session. Graph wants a multiple of 320 KiB.</summary>
    private const int UploadChunkBytes = 10 * 320 * 1024;

    private const string UserSelect = "id,displayName,mail,userPrincipalName,jobTitle,department";

    private string? _graphAccessToken;

    #region Graph token + request helper

    private async Task<string> GetGraphAccessTokenAsync()
    {
        if (!string.IsNullOrEmpty(_graphAccessToken) && GetJwtExpiryUtc(_graphAccessToken) > DateTime.UtcNow.AddMinutes(2))
            return _graphAccessToken;

        EnsureBootstrapped();
        if (string.IsNullOrEmpty(_refreshToken))
            throw new InvalidOperationException(
                "No refresh_token is available, so a Microsoft Graph token cannot be created. " +
                "Delete the cached Teams session file and sign in again (full login).");

        var fields = new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["scope"] = GraphScope,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = _refreshToken!,
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://login.microsoftonline.com/common/oauth2/v2.0/token")
        {
            Content = new FormUrlEncodedContent(fields),
        };
        req.Headers.Referrer = new Uri("https://teams.microsoft.com/");
        req.Headers.Add("Origin", "https://teams.microsoft.com");
        using var resp = await _http.SendAsync(req);
        string body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Graph-scoped refresh_token grant failed (status {(int)resp.StatusCode}). " +
                $"This account is probably not pre-consented for Microsoft Graph. Body: {body}");

        using var doc = JsonDocument.Parse(body);
        _graphAccessToken = doc.RootElement.GetProperty("access_token").GetString()!;
        Log("Acquired Graph-scoped access_token (aud=https://graph.microsoft.com).");
        return _graphAccessToken;
    }

    /// <summary>Sends one Graph request with the Graph bearer token. Throws on a non-success status.</summary>
    private async Task<string> SendGraphAsync(HttpMethod method, string url, HttpContent? content = null,
        bool consistencyEventual = false, bool allowNotFound = false)
    {
        string token = await GetGraphAccessTokenAsync();
        using var req = new HttpRequestMessage(method, url) { Content = content };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("Accept", "application/json");
        if (consistencyEventual) req.Headers.Add("ConsistencyLevel", "eventual");

        using var resp = await _http.SendAsync(req);
        string body = await resp.Content.ReadAsStringAsync();
        if (allowNotFound && (int)resp.StatusCode == 404) return "";
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{method} {url} failed (status {(int)resp.StatusCode}). Body: {body}");
        return body;
    }

    private static StringContent JsonContent(object payload) =>
        new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    private static string Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    #endregion

    #region Users

    /// <summary>Looks one user up by AAD object id, "8:orgid:{id}" MRI, or user principal name / mail. Null if not found.</summary>
    public async Task<TeamsUser?> GetUserAsync(string idOrMriOrUpn)
    {
        EnsureBootstrapped();
        string key = idOrMriOrUpn.Trim();
        const string mriPrefix = "8:orgid:";
        if (key.StartsWith(mriPrefix, StringComparison.OrdinalIgnoreCase))
            key = key[mriPrefix.Length..];

        string body = await SendGraphAsync(HttpMethod.Get,
            $"{GraphBase}/users/{Uri.EscapeDataString(key)}?$select={UserSelect}", allowNotFound: true);
        if (body.Length == 0) return null;

        using var doc = JsonDocument.Parse(body);
        return ParseUser(doc.RootElement);
    }

    /// <summary>Searches the directory by display name, mail or user principal name.</summary>
    public async Task<List<TeamsUser>> SearchUsersAsync(string query, int top = 10)
    {
        EnsureBootstrapped();
        string q = query.Trim().Replace("\"", "");
        top = Math.Clamp(top, 1, 25);

        // 1) Full text search (needs ConsistencyLevel: eventual).
        string search = Uri.EscapeDataString($"\"displayName:{q}\" OR \"mail:{q}\" OR \"userPrincipalName:{q}\"");
        string body = await SendGraphAsync(HttpMethod.Get,
            $"{GraphBase}/users?$search={search}&$select={UserSelect}&$top={top}", consistencyEventual: true);
        var users = ParseUserList(body);
        if (users.Count > 0) return users;

        // 2) Prefix filter. Search can miss names written in Japanese; startswith finds them.
        string odata = q.Replace("'", "''");
        string filter = Uri.EscapeDataString(
            $"startswith(displayName,'{odata}') or startswith(mail,'{odata}') or startswith(userPrincipalName,'{odata}')");
        body = await SendGraphAsync(HttpMethod.Get,
            $"{GraphBase}/users?$filter={filter}&$select={UserSelect}&$top={top}", consistencyEventual: true);
        return ParseUserList(body);
    }

    private static List<TeamsUser> ParseUserList(string body)
    {
        var list = new List<TeamsUser>();
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("value", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var el in arr.EnumerateArray())
                list.Add(ParseUser(el));
        return list;
    }

    private static TeamsUser ParseUser(JsonElement el) => new()
    {
        Id = Str(el, "id"),
        DisplayName = Str(el, "displayName"),
        Mail = Str(el, "mail"),
        UserPrincipalName = Str(el, "userPrincipalName"),
        JobTitle = Str(el, "jobTitle"),
        Department = Str(el, "department"),
    };

    #endregion

    #region Chat list (Graph)

    private async Task<List<TeamsRoom>> ListRoomsViaGraphAsync(int maxRooms)
    {
        var rooms = new List<TeamsRoom>();
        string? url = $"{GraphBase}/me/chats?$expand=members&$top=50";
        int pages = 0;
        while (url != null && rooms.Count < maxRooms && pages < 40)
        {
            pages++;
            string body = await SendGraphAsync(HttpMethod.Get, url);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("value", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var chat in arr.EnumerateArray())
                {
                    string chatType = Str(chat, "chatType");
                    string topic = Str(chat, "topic");
                    var others = new List<string>();
                    int memberCount = 0;
                    if (chat.TryGetProperty("members", out var members) && members.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var m in members.EnumerateArray())
                        {
                            memberCount++;
                            string memberId = Str(m, "userId");
                            string memberName = Str(m, "displayName");
                            if (!string.Equals(memberId, _objectId, StringComparison.OrdinalIgnoreCase) && memberName.Length > 0)
                                others.Add(memberName);
                        }
                    }

                    string kind = chatType switch
                    {
                        "oneOnOne" => memberCount <= 1 ? "self" : "oneOnOne",
                        "group" => "group",
                        "meeting" => "meeting",
                        _ => "unknown",
                    };
                    string name = topic.Length > 0 ? topic
                        : others.Count > 0 ? string.Join(", ", others)
                        : kind == "self" ? "Notes to self"
                        : kind == "meeting" ? "(meeting chat without a name)"
                        : "(no name)";
                    rooms.Add(new TeamsRoom { Id = Str(chat, "id"), Name = name, Kind = kind });
                }
            }
            url = doc.RootElement.TryGetProperty("@odata.nextLink", out var next) ? next.GetString() : null;
        }
        return rooms;
    }

    #endregion

    #region OneDrive upload

    /// <summary>
    /// Uploads one local file to OneDrive (folder "Microsoft Teams Chat Files"; an existing file is never overwritten,
    /// the new one is renamed) and creates an organization-scoped view link for it.
    /// </summary>
    private async Task<TeamsFileInfo> UploadFileAndCreateLinkAsync(string localPath)
    {
        var info = new FileInfo(localPath);
        string fileName = info.Name;
        string target = $"{GraphBase}/me/drive/root:/{Uri.EscapeDataString(UploadFolder)}/{Uri.EscapeDataString(fileName)}:";

        string uploadBody;
        if (info.Length <= SimpleUploadMaxBytes)
        {
            byte[] bytes = await File.ReadAllBytesAsync(localPath);
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            Log($"Uploading \"{fileName}\" ({bytes.Length} bytes) to OneDrive...");
            uploadBody = await SendGraphAsync(HttpMethod.Put,
                $"{target}/content?@microsoft.graph.conflictBehavior=rename", content);
        }
        else
        {
            Log($"Uploading \"{fileName}\" ({info.Length} bytes) to OneDrive in chunks...");
            uploadBody = await UploadInChunksAsync(target, localPath, info.Length);
        }

        using var uploadDoc = JsonDocument.Parse(uploadBody);
        string itemId = uploadDoc.RootElement.GetProperty("id").GetString()!;
        string storedName = Str(uploadDoc.RootElement, "name");

        string linkBody = await SendGraphAsync(HttpMethod.Post,
            $"{GraphBase}/me/drive/items/{Uri.EscapeDataString(itemId)}/createLink",
            JsonContent(new { type = "view", scope = "organization" }));
        using var linkDoc = JsonDocument.Parse(linkBody);
        string shareUrl = linkDoc.RootElement.GetProperty("link").GetProperty("webUrl").GetString()!;

        return new TeamsFileInfo { Name = storedName.Length > 0 ? storedName : fileName, Url = shareUrl };
    }

    private async Task<string> UploadInChunksAsync(string target, string localPath, long length)
    {
        string sessionBody = await SendGraphAsync(HttpMethod.Post, $"{target}/createUploadSession",
            JsonContent(new Dictionary<string, object>
            {
                ["item"] = new Dictionary<string, object> { ["@microsoft.graph.conflictBehavior"] = "rename" },
            }));
        using var sessionDoc = JsonDocument.Parse(sessionBody);
        string uploadUrl = sessionDoc.RootElement.GetProperty("uploadUrl").GetString()!;

        await using var stream = File.OpenRead(localPath);
        var buffer = new byte[UploadChunkBytes];
        long offset = 0;
        while (offset < length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(UploadChunkBytes, length - offset)));
            if (read <= 0) throw new IOException($"Unexpected end of file while reading \"{localPath}\".");

            // The uploadUrl already carries its own authorization. Do NOT add the bearer token here.
            using var req = new HttpRequestMessage(HttpMethod.Put, uploadUrl) { Content = new ByteArrayContent(buffer, 0, read) };
            req.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + read - 1, length);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var resp = await _http.SendAsync(req);
            string body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Chunk upload failed at byte {offset} (status {(int)resp.StatusCode}). Body: {body}");

            offset += read;
            if (offset >= length) return body; // last chunk answers with the finished drive item
        }
        throw new InvalidOperationException("Upload finished without a drive item in the answer.");
    }

    #endregion
}
