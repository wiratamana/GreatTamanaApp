
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GTA.Tools.Impl.Teams;

// Chat part of the client: list rooms, read messages, post messages (with or without files).
public sealed partial class TeamsSessionClient
{
    /// <summary>Id of the "Notes to self" chat. Same id for every account.</summary>
    public const string SelfChatId = "48:notes";

    private const string MessageView = "msnp24Equivalent%7CsupportsMessageProperties";

    /// <summary>Safety stop while paging back through a room (200 messages per page).</summary>
    private const int MaxMessagePages = 100;

    #region Rooms

    /// <summary>All rooms the signed-in user can see (id + name + kind). "Notes to self" is always first.</summary>
    public async Task<List<TeamsRoom>> ListRoomsAsync(int maxRooms = 500)
    {
        EnsureBootstrapped();
        List<TeamsRoom> rooms;

        try
        {
            rooms = await ListRoomsViaGraphAsync(maxRooms);
        }
        catch (Exception ex)
        {
            Log($"Graph chat list failed ({ex.Message}); trying the Teams chat service instead.");
            rooms = await ListRoomsViaChatServiceAsync(maxRooms);
        }

        // "Notes to self" first, and only once (Graph returns it too, with a member-based name).
        rooms.RemoveAll(r => r.Kind == "self" || string.Equals(r.Id, SelfChatId, StringComparison.OrdinalIgnoreCase));
        rooms.Insert(0, new TeamsRoom { Id = SelfChatId, Name = "Notes to self", Kind = "self" });
        return rooms;
    }

    private async Task<List<TeamsRoom>> ListRoomsViaChatServiceAsync(int maxRooms)
    {
        var rooms = new List<TeamsRoom>();
        string? url = $"{_region!.ChatServiceBaseUrl}/v1/users/ME/conversations?view={MessageView}&pageSize=100&startTime=1";
        int pages = 0;
        while (url != null && rooms.Count < maxRooms && pages < 40)
        {
            pages++;
            string body = await GetTeamsApiAsync(url, bearerTokenOverride: _chatsvcAccessToken);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("conversations", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var conv in arr.EnumerateArray())
                {
                    string id = Str(conv, "id");
                    if (id.Length == 0 || id.StartsWith("48:", StringComparison.Ordinal)) continue; // system conversations (notes, calls, ...)

                    string topic = "", threadType = "";
                    if (conv.TryGetProperty("threadProperties", out var tp) && tp.ValueKind == JsonValueKind.Object)
                    {
                        topic = Str(tp, "topic");
                        threadType = Str(tp, "threadType");
                    }

                    string kind = threadType switch
                    {
                        "chat" => id.Contains("@unq.gbl.spaces", StringComparison.OrdinalIgnoreCase) ? "oneOnOne" : "group",
                        "meeting" => "meeting",
                        "topic" or "space" or "sharedspace" => "channel",
                        _ => "unknown",
                    };
                    rooms.Add(new TeamsRoom { Id = id, Name = topic.Length > 0 ? topic : "(no name)", Kind = kind });
                }
            }

            url = doc.RootElement.TryGetProperty("_metadata", out var meta) ? Str(meta, "backwardLink") : null;
            if (string.IsNullOrEmpty(url)) url = null;
        }
        return rooms;
    }

    #endregion

    #region Read messages

    /// <summary>
    /// Reads the newest messages of a room inside [fromUtc, toUtc], at most <paramref name="maxMessages"/>.
    /// Result is in time order (oldest first). Pass null for an open end of the range.
    /// </summary>
    public async Task<List<TeamsMessage>> ReadMessagesAsync(string roomId, DateTimeOffset? fromUtc, DateTimeOffset? toUtc, int maxMessages)
    {
        EnsureBootstrapped();
        var found = new List<TeamsMessage>();

        string? url = $"{_region!.ChatServiceBaseUrl}/v1/users/ME/conversations/{Uri.EscapeDataString(roomId)}/messages" +
                      $"?view={MessageView}&pageSize=200&startTime=1";
        int pages = 0;
        bool reachedStart = false;

        // The service answers newest first, so we can stop as soon as a message is older than the range.
        while (url != null && !reachedStart && found.Count < maxMessages && pages < MaxMessagePages)
        {
            pages++;
            string body = await GetTeamsApiAsync(url, bearerTokenOverride: _chatsvcAccessToken);
            using var doc = JsonDocument.Parse(body);

            if (doc.RootElement.TryGetProperty("messages", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var raw in arr.EnumerateArray())
                {
                    TeamsMessage? msg = ParseMessage(raw);
                    if (msg == null) continue;
                    if (toUtc.HasValue && msg.TimeUtc > toUtc.Value) continue;
                    if (fromUtc.HasValue && msg.TimeUtc < fromUtc.Value) { reachedStart = true; break; }

                    found.Add(msg);
                    if (found.Count >= maxMessages) break;
                }
            }

            url = doc.RootElement.TryGetProperty("_metadata", out var meta) ? Str(meta, "backwardLink") : null;
            if (string.IsNullOrEmpty(url)) url = null;
        }

        found.Sort((a, b) => a.TimeUtc.CompareTo(b.TimeUtc));
        return found;
    }

    private static TeamsMessage? ParseMessage(JsonElement raw)
    {
        string messageType = Str(raw, "messagetype");
        // System lines: member joined, call started, topic changed, ...
        if (messageType.StartsWith("ThreadActivity/", StringComparison.OrdinalIgnoreCase) ||
            messageType.StartsWith("Control/", StringComparison.OrdinalIgnoreCase) ||
            messageType.StartsWith("Event/", StringComparison.OrdinalIgnoreCase))
            return null;

        string timeText = Str(raw, "composetime");
        if (timeText.Length == 0) timeText = Str(raw, "originalarrivaltime");
        if (!DateTimeOffset.TryParse(timeText, null, System.Globalization.DateTimeStyles.AssumeUniversal, out var time))
            return null;

        string html = Str(raw, "content");
        var msg = new TeamsMessage
        {
            Id = Str(raw, "id"),
            TimeUtc = time.ToUniversalTime(),
            Text = HtmlToPlainText(html),
        };

        string from = Str(raw, "from"); // ".../contacts/8:orgid:{oid}"
        int slash = from.LastIndexOf('/');
        string fromMri = slash >= 0 ? from[(slash + 1)..] : from;
        msg.SenderId = fromMri.StartsWith("8:orgid:", StringComparison.OrdinalIgnoreCase) ? fromMri["8:orgid:".Length..] : fromMri;
        msg.SenderName = Str(raw, "fromDisplayNameInToken");
        if (msg.SenderName.Length == 0) msg.SenderName = Str(raw, "imdisplayname");

        if (raw.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
        {
            if (props.TryGetProperty("deletetime", out _) && msg.Text.Length == 0) return null; // deleted message
            ReadFiles(props, msg);
        }

        // A message with no text and no file has nothing to show (reactions, empty edits).
        return msg.Text.Length == 0 && msg.Files.Count == 0 ? null : msg;
    }

    /// <summary>"properties.files" is a JSON array that is stored inside a string.</summary>
    private static void ReadFiles(JsonElement props, TeamsMessage msg)
    {
        string filesJson = Str(props, "files");
        if (filesJson.Length < 3) return;
        try
        {
            using var filesDoc = JsonDocument.Parse(filesJson);
            if (filesDoc.RootElement.ValueKind != JsonValueKind.Array) return;
            foreach (var f in filesDoc.RootElement.EnumerateArray())
            {
                string name = Str(f, "fileName");
                if (name.Length == 0) name = Str(f, "title");
                string url = "";
                if (f.TryGetProperty("fileInfo", out var fi)) url = Str(fi, "shareUrl");
                if (url.Length == 0) url = Str(f, "objectUrl");
                msg.Files.Add(new TeamsFileInfo { Name = name, Url = url });
            }
        }
        catch (JsonException)
        {
            // Unknown format: the message text is still useful without the file list.
        }
    }

    private static string HtmlToPlainText(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        string text = Regex.Replace(html, @"<br\s*/?>|</p>|</div>|</li>|</h\d>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<.*?>", "", RegexOptions.Singleline);
        text = WebUtility.HtmlDecode(text).Replace('\u00A0', ' ');
        text = Regex.Replace(text, @"[ \t]+", " ");
        text = Regex.Replace(text, @"\s*\n\s*", "\n");
        return text.Trim();
    }

    #endregion

    #region Post message

    /// <summary>
    /// Posts a message. Without files: through the Teams chat service (works for chats and channels).
    /// With files: each file is uploaded to OneDrive and attached through Microsoft Graph (chats only, no channels).
    /// </summary>
    public async Task<TeamsPostResult> PostMessageAsync(string roomId, string text, IReadOnlyList<string> filePaths,
        TeamsTextFormat format = TeamsTextFormat.Text)
    {
        EnsureBootstrapped();
        var result = new TeamsPostResult { RoomId = roomId };
        string bodyHtml = TeamsMessageFormatter.ToHtml(text, format);

        if (filePaths.Count == 0)
        {
            result.MessageId = await PostHtmlViaChatServiceAsync(roomId, bodyHtml);
            return result;
        }

        // Upload everything first. If one upload fails, nothing is posted.
        foreach (string path in filePaths)
            result.UploadedFiles.Add(await UploadFileAndCreateLinkAsync(path));

        var attachments = new List<object>();
        var html = new StringBuilder(bodyHtml);
        foreach (TeamsFileInfo file in result.UploadedFiles)
        {
            string attachmentId = Guid.NewGuid().ToString("D");
            html.Append($"<attachment id=\"{attachmentId}\"></attachment>");
            attachments.Add(new { id = attachmentId, contentType = "reference", contentUrl = file.Url, name = file.Name });
        }

        string body = await SendGraphAsync(HttpMethod.Post,
            $"{GraphBase}/chats/{Uri.EscapeDataString(roomId)}/messages",
            JsonContent(new { body = new { contentType = "html", content = html.ToString() }, attachments }));
        using var doc = JsonDocument.Parse(body);
        result.MessageId = Str(doc.RootElement, "id");
        return result;
    }

    private async Task<string> PostHtmlViaChatServiceAsync(string roomId, string bodyHtml)
    {
        string url = $"{_region!.ChatServiceBaseUrl}/v1/users/ME/conversations/{Uri.EscapeDataString(roomId)}/messages";
        var payload = new
        {
            content = bodyHtml,
            messagetype = "RichText/Html",
            contenttype = "text",
            amsreferences = Array.Empty<string>(),
            clientmessageid = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(),
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent(payload) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _chatsvcAccessToken);
        req.Headers.Add("Accept", "application/json");
        req.Headers.Add("x-ms-client-type", "web");
        req.Headers.Referrer = new Uri($"https://teams.microsoft.com/v2/?tenantId={_tenantId}");
        using var resp = await _http.SendAsync(req);
        string respBody = await resp.Content.ReadAsStringAsync();
        _dumper?.Dump("teams_post_message.json", $"REQUEST:\n{JsonSerializer.Serialize(payload)}\n\nRESPONSE (status {(int)resp.StatusCode}):\n{respBody}");
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"POST {url} failed (status {(int)resp.StatusCode}). Body: {respBody}");

        try
        {
            using var doc = JsonDocument.Parse(respBody);
            return doc.RootElement.TryGetProperty("OriginalArrivalTime", out var t) ? t.ToString() : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    #endregion

    #region Edit message

    /// <summary>How far around the message id (= send time in ms) we look for the message. The id and the compose time differ a little.</summary>
    private static readonly TimeSpan EditLookupWindow = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Replaces the text of ONE existing message of the signed-in user (PUT .../messages/{id} on the Teams chat service,
    /// the same call the Teams web client uses). Before it changes anything it checks that the message exists, is the
    /// user's own, and has no attached files (an edit would risk dropping them). Afterwards it reads the message back.
    /// </summary>
    public async Task<TeamsEditResult> EditMessageAsync(string roomId, string messageId, string text, TeamsTextFormat format = TeamsTextFormat.Text)
    {
        EnsureBootstrapped();

        if (!long.TryParse(messageId, NumberStyles.None, CultureInfo.InvariantCulture, out long idMs) || idMs <= 0)
            throw new ArgumentException($"message_id must be the digits-only id from teams_read_messages (got \"{messageId}\").");

        TeamsMessage original = await FindMessageAsync(roomId, messageId, idMs)
            ?? throw new InvalidOperationException(
                $"Message {messageId} was not found in room {roomId} (deleted, wrong room, or not a normal text message). Nothing was changed.");

        if (!string.Equals(original.SenderId, _objectId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Message {messageId} was written by \"{(original.SenderName.Length > 0 ? original.SenderName : original.SenderId)}\", not by you. " +
                "Teams only lets you edit your own messages. Nothing was changed.");

        if (original.Files.Count > 0)
            throw new InvalidOperationException(
                $"Message {messageId} has {original.Files.Count} attached file(s). Editing it could remove the files, so this tool refuses. Nothing was changed.");

        string html = TeamsMessageFormatter.ToHtml(text, format);
        string url = $"{_region!.ChatServiceBaseUrl}/v1/users/ME/conversations/{Uri.EscapeDataString(roomId)}/messages/{Uri.EscapeDataString(messageId)}";
        var payload = new
        {
            content = html,
            messagetype = "RichText/Html",
            contenttype = "text",
            amsreferences = Array.Empty<string>(),
            clientmessageid = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            properties = new { importance = "", subject = "" },
        };

        using var req = new HttpRequestMessage(HttpMethod.Put, url) { Content = JsonContent(payload) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _chatsvcAccessToken);
        req.Headers.Add("Accept", "application/json");
        req.Headers.Add("x-ms-client-type", "web");
        req.Headers.Referrer = new Uri($"https://teams.microsoft.com/v2/?tenantId={_tenantId}");
        using var resp = await _http.SendAsync(req);
        string respBody = await resp.Content.ReadAsStringAsync();
        _dumper?.Dump("teams_edit_message.json", $"REQUEST:\n{JsonSerializer.Serialize(payload)}\n\nRESPONSE (status {(int)resp.StatusCode}):\n{respBody}");
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"PUT {url} failed (status {(int)resp.StatusCode}). Body: {respBody}");

        var result = new TeamsEditResult { RoomId = roomId, MessageId = messageId, PreviousText = original.Text };

        // Read it back, so the caller knows the edit really is visible (the service can need a moment).
        string expected = HtmlToPlainText(html);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            await Task.Delay(1500);
            TeamsMessage? after = await FindMessageAsync(roomId, messageId, idMs);
            if (after == null) continue;
            result.CurrentText = after.Text;
            if (string.Equals(after.Text, expected, StringComparison.Ordinal))
            {
                result.Verified = true;
                break;
            }
        }
        return result;
    }

    /// <summary>Finds one message by id. The id is the send time in Unix milliseconds, so only a short time window is read.</summary>
    private async Task<TeamsMessage?> FindMessageAsync(string roomId, string messageId, long idMs)
    {
        DateTimeOffset center;
        try
        {
            center = DateTimeOffset.FromUnixTimeMilliseconds(idMs);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new ArgumentException($"message_id \"{messageId}\" is not a valid message id.");
        }

        List<TeamsMessage> candidates = await ReadMessagesAsync(roomId, center - EditLookupWindow, center + EditLookupWindow, 200);
        return candidates.FirstOrDefault(m => m.Id == messageId);
    }

    #endregion
}
