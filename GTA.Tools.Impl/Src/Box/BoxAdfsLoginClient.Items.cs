// Folder / item part of BoxAdfsLoginClient: full folder listing, create folder, file + folder info, delete file + folder.
// Ported from the BoxUploadSmokeTest / BoxAutomation prototype. Every route below was tested against the real Box.
//
//  LISTING   GET  /app-api/enduserapp/folder/{id}                     20 items per page + "nextMarker" (base64); next page = ?marker=<nextMarker>
//            (The GraphQL listing in GetFolderItemsAsync only works for the ROOT folder "0" -- Box answers
//             "rootFolderEntries can only be used with the root folder" for any other id. Do not use it for real folders.)
//  CREATE    POST /app-api/enduserapp/folder/create                   {"folderName":"...","parentFolderID":<id>}  ->  {"folderID":<id>}
//            An existing name answers HTTP 400 errorCode "createFolderNameError".
//  DELETE    POST /app-api/enduserapp/items/delete                    {"itemTypedIDs":["f_<id>"]} or ["d_<id>"]  ->  {"successItemTypedIDs":[...]}
//            Goes to the Trash (restorable in the Box web UI) unless the enterprise has trash disabled. A folder takes ALL its content with it.
//  FILE INFO GET  https://api.box.com/2.0/files/{id}?fields=...       with a READ bearer token from POST /app-api/enduserapp/elements/tokens

using System.Text;
using System.Text.Json;

namespace GTA.Tools.Impl.Box;

/// <summary>Facts about one Box file (from the public API).</summary>
public sealed record BoxFileInfo(string Id, string Name, string? Sha1, string? VersionId, string? VersionNumber, string? ParentId, string? ParentName);

/// <summary>Facts about one Box folder (from the web app's folder call).</summary>
public sealed record BoxFolderInfo(string Id, string Name, int ItemCount, string? ParentId, string? ParentName);

public sealed partial class BoxAdfsLoginClient
{
    private static void RequireNumericId(string id, string what)
    {
        if (string.IsNullOrEmpty(id) || !id.All(char.IsAsciiDigit))
            throw new ArgumentException($"{what} must be a numeric Box id (got '{id}').");
    }

    // ------------------------------------------------------------------
    // Listing (all pages)
    // ------------------------------------------------------------------

    /// <summary>ALL direct children (files + sub-folders) of a folder. Follows nextMarker; fails loudly if the server ignores the marker.</summary>
    public async Task<List<BoxItem>> ListAllItemsAsync(string folderId)
    {
        RequireNumericId(folderId, "folder id");
        var all = new List<BoxItem>();
        string? marker = null;
        string? firstOfPrevPage = null;

        for (int guard = 0; guard < 2000; guard++)   // 2000 pages x 20 items = 40k: far beyond any real folder, only stops a runaway loop
        {
            string url = $"https://{_boxHost}/app-api/enduserapp/folder/{folderId}" + (marker == null ? "" : "?marker=" + Uri.EscapeDataString(marker));
            var (status, body) = await SendSessionAsync(HttpMethod.Get, url);
            if (status != 200) throw new InvalidOperationException($"Folder listing failed (HTTP {status}): {Trunc(body)}");

            using var doc = JsonDocument.Parse(body);
            string? firstId = null;
            if (doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var it in items.EnumerateArray())
                {
                    string id = it.TryGetProperty("id", out var i) ? i.ToString() : "";
                    firstId ??= id;
                    all.Add(new BoxItem
                    {
                        Id = id,
                        TypedId = it.TryGetProperty("typedID", out var tid) ? tid.GetString() ?? "" : "",
                        Type = it.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "",
                        Name = it.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                        Extension = it.TryGetProperty("extension", out var ex) && ex.ValueKind == JsonValueKind.String ? ex.GetString() : null,
                    });
                }
            }

            // Same first item on two pages = the marker was ignored. Stop with an error instead of looping or returning a short list.
            if (marker != null && firstId != null && firstId == firstOfPrevPage)
                throw new InvalidOperationException($"Folder {folderId}: the server ignored ?marker= (got the same page again).");
            firstOfPrevPage = firstId;

            string? next = doc.RootElement.TryGetProperty("nextMarker", out var nm) && nm.ValueKind == JsonValueKind.String ? nm.GetString() : null;
            if (string.IsNullOrEmpty(next)) return all;
            marker = next;
        }
        throw new InvalidOperationException($"Folder {folderId}: listing did not end after 2000 pages.");
    }

    // ------------------------------------------------------------------
    // Create folder
    // ------------------------------------------------------------------

    /// <summary>Create a sub-folder and return its id. Throws when it fails; an existing name gives HTTP 400 "createFolderNameError" in the message.</summary>
    public async Task<string> CreateFolderAsync(string parentFolderId, string folderName)
    {
        RequireNumericId(parentFolderId, "parent folder id");
        // parentFolderID is a JSON number in the web UI's own call (digits only: checked above, so this is safe to inline).
        string json = "{\"folderName\":" + JsonSerializer.Serialize(folderName) + ",\"parentFolderID\":" + parentFolderId + "}";
        var (status, body) = await SendSessionAsync(HttpMethod.Post,
            $"https://{_boxHost}/app-api/enduserapp/folder/create",
            new StringContent(json, Encoding.UTF8, "application/json"));
        if (status != 200 && status != 201) throw new InvalidOperationException($"Create folder '{folderName}' failed (HTTP {status}): {Trunc(body)}");

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("folderID", out var fid))
            throw new InvalidOperationException($"Create folder '{folderName}': no folderID in the answer: {Trunc(body)}");
        return fid.ValueKind == JsonValueKind.Number ? fid.GetInt64().ToString() : fid.GetString()!;
    }

    // ------------------------------------------------------------------
    // Info
    // ------------------------------------------------------------------

    public async Task<BoxFolderInfo> GetFolderInfoAsync(string folderId)
    {
        RequireNumericId(folderId, "folder id");
        var (status, body) = await SendSessionAsync(HttpMethod.Get, $"https://{_boxHost}/app-api/enduserapp/folder/{folderId}");
        if (status != 200) throw new InvalidOperationException($"Folder info failed (HTTP {status}): {Trunc(body)}");

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("folder", out var f) || !f.TryGetProperty("name", out var n) || n.GetString() is not string name)
            throw new InvalidOperationException($"Folder {folderId}: no folder name in the answer: {Trunc(body)}");
        int count = doc.RootElement.TryGetProperty("folderItemCount", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : -1;
        string? parentId = f.TryGetProperty("parentFolderID", out var pid) ? pid.ToString() : null;
        string? parentName = f.TryGetProperty("parentFolderName", out var pn) && pn.ValueKind == JsonValueKind.String ? pn.GetString() : null;
        return new BoxFolderInfo(folderId, name, count, parentId, parentName);
    }

    /// <summary>Read bearer token for one item (from the session). Only used for the file-info call.</summary>
    private async Task<string> GetReadTokenAsync(string itemId)
    {
        var (status, text) = await SendSessionAsync(HttpMethod.Post,
            $"https://{_boxHost}/app-api/enduserapp/elements/tokens",
            new StringContent(JsonSerializer.Serialize(new { fileIDs = new[] { itemId } }), Encoding.UTF8, "application/json"));
        if (status != 200) throw new InvalidOperationException($"elements/tokens failed (HTTP {status}): {Trunc(text)}");

        using var doc = JsonDocument.Parse(text);
        if (doc.RootElement.TryGetProperty(itemId, out var el) && el.ValueKind == JsonValueKind.Object &&
            el.TryGetProperty("read", out var r) && r.GetString() is string token && token.Length > 0)
            return token;
        throw new InvalidOperationException($"No read token for item {itemId} (no access to it, or it does not exist).");
    }

    public async Task<BoxFileInfo> GetFileInfoAsync(string fileId)
    {
        RequireNumericId(fileId, "file id");
        string token = await GetReadTokenAsync(fileId);
        var (status, body) = await SendBearerAsync(HttpMethod.Get,
            $"{ApiBase}/files/{fileId}?fields=id,name,sha1,version_number,file_version,parent,item_status", token);
        if (status != 200) throw new InvalidOperationException($"Get file info failed (HTTP {status}): {Trunc(body)}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        string name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        string? sha1 = root.TryGetProperty("sha1", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        string? verNo = root.TryGetProperty("version_number", out var vn) && vn.ValueKind == JsonValueKind.String ? vn.GetString() : null;
        string? verId = root.TryGetProperty("file_version", out var fv) && fv.ValueKind == JsonValueKind.Object && fv.TryGetProperty("id", out var vi) ? vi.GetString() : null;
        string? parentId = null, parentName = null;
        if (root.TryGetProperty("parent", out var p) && p.ValueKind == JsonValueKind.Object)
        {
            parentId = p.TryGetProperty("id", out var pi) ? pi.GetString() : null;
            parentName = p.TryGetProperty("name", out var pn) ? pn.GetString() : null;
        }
        return new BoxFileInfo(fileId, name, sha1, verId, verNo, parentId, parentName);
    }

    // ------------------------------------------------------------------
    // Delete (-> Trash)
    // ------------------------------------------------------------------

    private async Task<bool> DeleteTypedAsync(string typedId)
    {
        var (status, body) = await SendSessionAsync(HttpMethod.Post,
            $"https://{_boxHost}/app-api/enduserapp/items/delete",
            new StringContent(JsonSerializer.Serialize(new { itemTypedIDs = new[] { typedId } }), Encoding.UTF8, "application/json"));
        if (status != 200) throw new InvalidOperationException($"Delete {typedId} failed (HTTP {status}): {Trunc(body)}");

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("successItemTypedIDs", out var ok)
               && ok.ValueKind == JsonValueKind.Array
               && ok.EnumerateArray().Any(e => e.GetString() == typedId);
    }

    /// <summary>Delete a file (to Trash). True when Box lists it in successItemTypedIDs.</summary>
    public Task<bool> DeleteFileAsync(string fileId)
    {
        RequireNumericId(fileId, "file id");
        return DeleteTypedAsync("f_" + fileId);
    }

    /// <summary>Delete a folder INCLUDING everything inside it (to Trash). True when Box lists it in successItemTypedIDs.</summary>
    public Task<bool> DeleteFolderAsync(string folderId)
    {
        RequireNumericId(folderId, "folder id");
        if (folderId.TrimStart('0').Length == 0) throw new ArgumentException("The root folder (id 0) can never be deleted.");
        return DeleteTypedAsync("d_" + folderId);
    }
}
