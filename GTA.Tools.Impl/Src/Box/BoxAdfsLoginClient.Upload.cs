// Upload part of BoxAdfsLoginClient (new file, new version, "upload only if the content differs").
// Ported from the BoxUploadSmokeTest / BoxAutomation prototype. Every route below was tested against the real Box.
//
//  HOW AUTH WORKS (reverse-engineered from the Box web app JS):
//    1. The logged-in session cookies + CSRF headers call
//         POST https://{BoxHost}/app-api/enduserapp/elements/upload-token   body {"folder_id":"..."}  (new file)
//                                                                         or {"file_id":"..."}    (new version)
//       and get {"access_token":"...","expires_in":N}.
//       The normal read/write tokens (elements/tokens) do NOT have the upload scope (Box answers 403 insufficient_scope).
//    2. That bearer token is used against the public Box upload API (no cookies, no CSRF there):
//         new file     POST https://upload.box.com/api/2.0/files/content          (multipart)
//         new version  POST https://upload.box.com/api/2.0/files/{id}/content     (multipart)
//
//  NOTE: Box's pre-flight (OPTIONS /files/content) was tried and is useless: it answers 200 even when the name exists.
//        An existing name is found by the folder listing (no bytes sent) or, as fallback, by Box's HTTP 409 (bytes sent once).

using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GTA.Tools.Impl.Box;

/// <summary>Result of one raw upload call (new file or new version).</summary>
public sealed record BoxUploadResult(string FileId, string Name, string? VersionId, string? Sha1, long Size);

public enum BoxSyncOutcome
{
    /// <summary>The name was free: new file created.</summary>
    Created,
    /// <summary>The name existed with different content: a new version was uploaded.</summary>
    NewVersion,
    /// <summary>The name existed with the SAME content (SHA-1): nothing was sent.</summary>
    IdenticalSkipped,
    /// <summary>The name existed with different content but overwrite was off: nothing was sent.</summary>
    ExistsSkipped,
}

/// <summary>Outcome of UploadOrUpdateAsync. For the two skipped outcomes FileId is the EXISTING file.</summary>
public sealed record BoxSyncResult(BoxSyncOutcome Outcome, string FileId, string Name, string? VersionId, string? Sha1, long Size);

/// <summary>HTTP 409 item_name_in_use. Carries the id of the file that is already there (null when Box did not say).</summary>
public sealed class BoxNameConflictException : InvalidOperationException
{
    public string? ExistingFileId { get; }
    public BoxNameConflictException(string message, string? existingFileId) : base(message) => ExistingFileId = existingFileId;
}

public sealed partial class BoxAdfsLoginClient
{
    private const string UploadBase = "https://upload.box.com/api/2.0";
    private const string ApiBase = "https://api.box.com/2.0";

    private static string Trunc(string s, int n = 800) => s.Length <= n ? s : s[..n] + "...";

    internal static string Sha1Hex(byte[] bytes) => Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();

    /// <summary>Request with the logged-in session cookies + the CSRF / client headers Box's own web UI sends. Redirects are NOT followed.</summary>
    private async Task<(int Status, string Body)> SendSessionAsync(HttpMethod method, string url, HttpContent? content = null)
    {
        if (!IsLoggedIn) throw new InvalidOperationException("Not logged in -- call LoginAsync() first.");
        string csrf = GetCsrfTokenFromCookieJar() ?? throw new InvalidOperationException("No csrf-token cookie available.");

        var req = new HttpRequestMessage(method, url) { Content = content };
        req.Headers.Referrer = new Uri($"https://{_boxHost}/");
        req.Headers.Add("Origin", $"https://{_boxHost}");
        req.Headers.Add("x-csrf-token", csrf);
        req.Headers.Add("request-token", csrf);
        req.Headers.Add("x-request-token", csrf);
        req.Headers.Add("x-box-client-name", "enduserapp");
        req.Headers.Add("x-box-client-version", "23.748.2+hotfix.1");

        var resp = await _http.SendAsync(req);
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync());
    }

    /// <summary>Bearer request. No cookies / CSRF headers are needed (or wanted) on upload.box.com / api.box.com.</summary>
    private async Task<(int Status, string Body)> SendBearerAsync(HttpMethod method, string url, string bearer, HttpContent? content = null)
    {
        var req = new HttpRequestMessage(method, url) { Content = content };
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearer);
        var resp = await _http.SendAsync(req);
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync());
    }

    /// <summary>Upload-scoped bearer token for a folder (new file) or a file (new version).</summary>
    private async Task<string> GetUploadTokenAsync(string? folderId = null, string? fileId = null)
    {
        var payload = new Dictionary<string, string>();
        if (folderId != null) payload["folder_id"] = folderId;
        if (fileId != null) payload["file_id"] = fileId;

        var (status, text) = await SendSessionAsync(HttpMethod.Post,
            $"https://{_boxHost}/app-api/enduserapp/elements/upload-token",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
        if (status != 200) throw new InvalidOperationException($"upload-token failed (HTTP {status}): {Trunc(text)}");

        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("access_token").GetString()
               ?? throw new InvalidOperationException("upload-token response has no access_token.");
    }

    private static MultipartFormDataContent BuildMultipart(string attributesJson, string fileName, byte[] bytes)
    {
        var mp = new MultipartFormDataContent();
        // Box requires the "attributes" part BEFORE the "file" part.
        mp.Add(new StringContent(attributesJson, Encoding.UTF8), "attributes");
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        mp.Add(file, "file", fileName);
        return mp;
    }

    private static BoxUploadResult ParseUploadResult(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var entry = doc.RootElement.GetProperty("entries")[0];
        string? verId = null, sha1 = null;
        if (entry.TryGetProperty("file_version", out var fv))
        {
            verId = fv.TryGetProperty("id", out var vi) ? vi.GetString() : null;
            sha1 = fv.TryGetProperty("sha1", out var sh) ? sh.GetString() : null;
        }
        return new BoxUploadResult(
            entry.GetProperty("id").GetString()!,
            entry.GetProperty("name").GetString()!,
            verId, sha1,
            entry.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number ? sz.GetInt64() : 0);
    }

    /// <summary>Builds the 409 exception and pulls the id of the EXISTING file out of context_info.conflicts (object or array).</summary>
    private static BoxNameConflictException MakeConflict(string message, string body)
    {
        string? id = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("context_info", out var ci) && ci.TryGetProperty("conflicts", out var cf))
            {
                var c = cf.ValueKind == JsonValueKind.Array ? (cf.GetArrayLength() > 0 ? cf[0] : default) : cf;
                if (c.ValueKind == JsonValueKind.Object && c.TryGetProperty("id", out var i)) id = i.GetString();
            }
        }
        catch (JsonException) { /* body was not JSON: id stays null */ }
        return new BoxNameConflictException(message, id);
    }

    /// <summary>Upload a NEW file into <paramref name="folderId"/>. Throws <see cref="BoxNameConflictException"/> (HTTP 409) when the name already exists.</summary>
    public async Task<BoxUploadResult> UploadFileAsync(string folderId, string fileName, byte[] bytes)
    {
        string token = await GetUploadTokenAsync(folderId: folderId);
        string attrs = JsonSerializer.Serialize(new { name = fileName, parent = new { id = folderId } });
        using var mp = BuildMultipart(attrs, fileName, bytes);
        var (status, body) = await SendBearerAsync(HttpMethod.Post, $"{UploadBase}/files/content", token, mp);
        if (status == 409) throw MakeConflict($"Upload failed (HTTP 409, a file with this name already exists): {Trunc(body)}", body);
        if (status != 201) throw new InvalidOperationException($"Upload failed (HTTP {status}): {Trunc(body)}");
        return ParseUploadResult(body);
    }

    /// <summary>Upload a NEW VERSION of an existing file. <paramref name="nameInBox"/> is the name the file must have afterwards (pass the CURRENT name to keep it).</summary>
    public async Task<BoxUploadResult> UploadNewVersionAsync(string fileId, string nameInBox, byte[] bytes)
    {
        string token = await GetUploadTokenAsync(fileId: fileId);
        string attrs = JsonSerializer.Serialize(new { name = nameInBox });
        using var mp = BuildMultipart(attrs, nameInBox, bytes);
        var (status, body) = await SendBearerAsync(HttpMethod.Post, $"{UploadBase}/files/{fileId}/content", token, mp);
        if (status != 201) throw new InvalidOperationException($"Upload new version failed (HTTP {status}): {Trunc(body)}");
        return ParseUploadResult(body);
    }

    /// <summary>
    /// Upload one file the way a sync tool should:
    ///   name free            -> Created
    ///   same content (SHA-1) -> IdenticalSkipped  (nothing is sent)
    ///   different content    -> NewVersion if <paramref name="overwrite"/>, else ExistsSkipped
    /// <paramref name="knownFileIds"/>: name -> file id of a COMPLETE listing of the folder the caller already made
    /// (then an existing file is found without sending any bytes). Null = just upload and let Box's 409 say it exists.
    /// </summary>
    public async Task<BoxSyncResult> UploadOrUpdateAsync(
        string folderId, string fileName, byte[] bytes, bool overwrite = true,
        IReadOnlyDictionary<string, string>? knownFileIds = null)
    {
        string? existingId = null;
        if (knownFileIds != null && knownFileIds.TryGetValue(fileName, out string? kid)) existingId = kid;

        if (existingId == null)
        {
            try
            {
                var up = await UploadFileAsync(folderId, fileName, bytes);
                return new BoxSyncResult(BoxSyncOutcome.Created, up.FileId, up.Name, up.VersionId, up.Sha1, up.Size);
            }
            catch (BoxNameConflictException c) when (c.ExistingFileId != null)
            {
                existingId = c.ExistingFileId;   // fallback: the caller had no listing (or it was stale)
            }
        }

        return await UpdateExistingAsync(existingId, null, bytes, overwrite);
    }

    /// <summary>
    /// Compare with an EXISTING file and upload a new version only when the content differs.
    /// <paramref name="explicitName"/> renames the file in Box; null keeps the current name.
    /// </summary>
    public async Task<BoxSyncResult> UpdateExistingAsync(string fileId, string? explicitName, byte[] bytes, bool overwrite = true)
    {
        string localSha1 = Sha1Hex(bytes);
        BoxFileInfo info = await GetFileInfoAsync(fileId);

        if (string.Equals(info.Sha1, localSha1, StringComparison.OrdinalIgnoreCase))
            return new BoxSyncResult(BoxSyncOutcome.IdenticalSkipped, fileId, info.Name, info.VersionId, info.Sha1, bytes.Length);
        if (!overwrite)
            return new BoxSyncResult(BoxSyncOutcome.ExistsSkipped, fileId, info.Name, info.VersionId, info.Sha1, bytes.Length);

        var up = await UploadNewVersionAsync(fileId, explicitName ?? info.Name, bytes);
        return new BoxSyncResult(BoxSyncOutcome.NewVersion, fileId, up.Name, up.VersionId, up.Sha1, up.Size);
    }
}
