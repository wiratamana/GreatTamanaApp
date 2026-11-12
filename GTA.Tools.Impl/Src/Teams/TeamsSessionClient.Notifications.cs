
using System.Text.Json;

namespace GTA.Tools.Impl.Teams;

// Notification part of the client: the "Activity" tab of Teams (@mentions, replies, reactions, meeting updates).
//
// How it works (proven with a smoke test against the real tenant):
//   The Activity feed is a hidden system chat with the well-known id "48:notifications". Every item in it is a normal
//   chat-service message. The useful data is in message.properties.activity (a JSON object, sometimes a JSON string):
//   activityType, activitySubtype, activityTimestamp, sourceUserImDisplayName, messagePreview, sourceThreadId, sourceMessageId ...
//   properties.isread tells if the item was already read in Teams.
//
// Things to know:
//   * There is NO server-side filter and NO push. We read the pages and filter here.
//   * An existing item is UPDATED IN PLACE (same id) when a new reply/reaction arrives on the same thread. So the
//     time that matters is activityTimestamp (last activity), not the message id or compose time.
//   * Nothing here changes data: no mark-as-read, no delete.
public sealed partial class TeamsSessionClient
{
    /// <summary>Id of the hidden activity-feed chat. Same id for every account.</summary>
    public const string NotificationsChatId = "48:notifications";

    /// <summary>Safety stop while paging back through the feed (about 50 items per page).</summary>
    private const int MaxNotificationPages = 4;

    /// <summary>
    /// Reads the activity feed, newest ACTIVITY first. All filters are optional and combined with AND.
    /// </summary>
    /// <param name="maxItems">Most items to return.</param>
    /// <param name="sinceUtc">Only items whose last activity is at or after this time.</param>
    /// <param name="unreadOnly">Only items that are not read in Teams yet.</param>
    /// <param name="types">Only these activity types (not case sensitive), e.g. "mention", "reply". Null/empty = all.</param>
    public async Task<List<TeamsNotification>> ReadNotificationsAsync(int maxItems, DateTimeOffset? sinceUtc,
        bool unreadOnly, IReadOnlyCollection<string>? types)
    {
        EnsureBootstrapped();
        var byId = new Dictionary<string, TeamsNotification>(StringComparer.Ordinal);

        string? url = $"{_region!.ChatServiceBaseUrl}/v1/users/ME/conversations/{Uri.EscapeDataString(NotificationsChatId)}/messages" +
                      $"?view={MessageView}&pageSize=50&startTime=1";
        int pages = 0;
        int matching = 0;

        while (url != null && matching < maxItems && pages < MaxNotificationPages)
        {
            pages++;
            string body = await GetTeamsApiAsync(url, bearerTokenOverride: _chatsvcAccessToken);
            using var doc = JsonDocument.Parse(body);

            if (doc.RootElement.TryGetProperty("messages", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var raw in arr.EnumerateArray())
                {
                    TeamsNotification? n = ParseNotification(raw);
                    if (n == null || byId.ContainsKey(n.Id)) continue;
                    byId[n.Id] = n;
                    if (Matches(n, sinceUtc, unreadOnly, types)) matching++;
                }
            }

            url = doc.RootElement.TryGetProperty("_metadata", out var meta) ? Str(meta, "backwardLink") : null;
            if (string.IsNullOrEmpty(url)) url = null;
        }

        return byId.Values
            .Where(n => Matches(n, sinceUtc, unreadOnly, types))
            .OrderByDescending(n => n.TimeUtc)
            .Take(maxItems)
            .ToList();
    }

    /// <summary>Teams sends some ids as a JSON string and some as a JSON number (for example sourceMessageId). Both are returned as text.</summary>
    private static string StrOrNumber(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var v)) return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.GetRawText(),
            _ => "",
        };
    }

    private static bool Matches(TeamsNotification n, DateTimeOffset? sinceUtc, bool unreadOnly, IReadOnlyCollection<string>? types)
    {
        if (sinceUtc.HasValue && n.TimeUtc < sinceUtc.Value) return false;
        if (unreadOnly && n.IsRead) return false;
        if (types != null && types.Count > 0 &&
            !types.Contains(n.ActivityType, StringComparer.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static TeamsNotification? ParseNotification(JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Object) return null;
        if (!raw.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object) return null;
        if (!props.TryGetProperty("activity", out var activityEl)) return null;

        // The activity object is normally a JSON object, but can come as a JSON string.
        JsonDocument? owned = null;
        try
        {
            JsonElement act = activityEl;
            if (activityEl.ValueKind == JsonValueKind.String)
            {
                string? text = activityEl.GetString();
                if (string.IsNullOrWhiteSpace(text)) return null;
                owned = JsonDocument.Parse(text);
                act = owned.RootElement;
            }
            if (act.ValueKind != JsonValueKind.Object) return null;

            string type = Str(act, "activityType");
            if (type.Length == 0) return null;

            DateTimeOffset time = default;
            bool haveTime = DateTimeOffset.TryParse(Str(act, "activityTimestamp"), null,
                System.Globalization.DateTimeStyles.AssumeUniversal, out time);
            if (!haveTime)
            {
                string fallback = Str(raw, "composetime");
                if (fallback.Length == 0) fallback = Str(raw, "originalarrivaltime");
                if (!DateTimeOffset.TryParse(fallback, null, System.Globalization.DateTimeStyles.AssumeUniversal, out time))
                    return null;
            }

            string sourceUserId = Str(act, "sourceUserId");
            const string orgPrefix = "8:orgid:";
            if (sourceUserId.StartsWith(orgPrefix, StringComparison.OrdinalIgnoreCase))
                sourceUserId = sourceUserId[orgPrefix.Length..];

            bool isRead = false;
            if (props.TryGetProperty("isread", out var readEl))
                isRead = readEl.ValueKind == JsonValueKind.True ||
                         (readEl.ValueKind == JsonValueKind.String && string.Equals(readEl.GetString(), "true", StringComparison.OrdinalIgnoreCase));

            return new TeamsNotification
            {
                Id = Str(raw, "id"),
                TimeUtc = time.ToUniversalTime(),
                ActivityType = type,
                ActivitySubtype = Str(act, "activitySubtype"),
                SourceUserId = sourceUserId,
                SourceUserName = Str(act, "sourceUserImDisplayName"),
                Preview = Str(act, "messagePreview"),
                SourceThreadId = Str(act, "sourceThreadId"),
                SourceMessageId = StrOrNumber(act, "sourceMessageId"),
                SourceThreadTopic = Str(act, "sourceThreadTopic"),
                IsRead = isRead,
            };
        }
        catch (JsonException)
        {
            return null; // unknown format: skip this one item, the rest of the feed is still useful
        }
        finally
        {
            owned?.Dispose();
        }
    }
}
