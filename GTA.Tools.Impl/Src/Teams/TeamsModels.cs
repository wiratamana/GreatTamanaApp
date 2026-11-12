
namespace GTA.Tools.Impl.Teams;

/// <summary>One Teams chat / group chat / meeting chat / channel the signed-in user can see.</summary>
public sealed class TeamsRoom
{
    /// <summary>The id used by every other Teams call (e.g. "19:...@thread.v2", "19:..._...@unq.gbl.spaces", "48:notes").</summary>
    public string Id { get; set; } = "";

    /// <summary>Topic of the room. For rooms without a topic: the names of the other members.</summary>
    public string Name { get; set; } = "";

    /// <summary>"self", "oneOnOne", "group", "meeting", "channel" or "unknown".</summary>
    public string Kind { get; set; } = "unknown";
}

/// <summary>One file that is attached to a message.</summary>
public sealed class TeamsFileInfo
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
}

/// <summary>One message of a room, already cleaned for reading.</summary>
public sealed class TeamsMessage
{
    public string Id { get; set; } = "";
    public DateTimeOffset TimeUtc { get; set; }
    public string SenderId { get; set; } = "";
    public string SenderName { get; set; } = "";

    /// <summary>Message body as plain text (HTML removed).</summary>
    public string Text { get; set; } = "";

    public List<TeamsFileInfo> Files { get; } = new();
}

/// <summary>Directory entry of a user (Microsoft Graph).</summary>
public sealed class TeamsUser
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Mail { get; set; } = "";
    public string UserPrincipalName { get; set; } = "";
    public string JobTitle { get; set; } = "";
    public string Department { get; set; } = "";
}

/// <summary>Result of one posted message.</summary>
public sealed class TeamsPostResult
{
    public string RoomId { get; set; } = "";

    /// <summary>Message id returned by Teams (can be empty when the service does not return one).</summary>
    public string MessageId { get; set; } = "";

    /// <summary>Files that were uploaded to OneDrive and attached (name as stored, share link).</summary>
    public List<TeamsFileInfo> UploadedFiles { get; } = new();
}

/// <summary>Result of one edited message.</summary>
public sealed class TeamsEditResult
{
    public string RoomId { get; set; } = "";
    public string MessageId { get; set; } = "";

    /// <summary>Text of the message before the edit (plain text).</summary>
    public string PreviousText { get; set; } = "";

    /// <summary>Text of the message as Teams returned it after the edit (plain text). Empty when it could not be read back.</summary>
    public string CurrentText { get; set; } = "";

    /// <summary>True when the read-back after the edit showed the new text. False = Teams said OK but the new text was not visible yet.</summary>
    public bool Verified { get; set; }
}

/// <summary>One item of the Teams "Activity" feed (a mention, reply, reaction or meeting update).</summary>
public sealed class TeamsNotification
{
    /// <summary>Id of the feed item (NOT the id of the message it is about).</summary>
    public string Id { get; set; } = "";

    /// <summary>Time of the LAST activity on this item. A feed item is updated in place when a new reply/reaction arrives.</summary>
    public DateTimeOffset TimeUtc { get; set; }

    /// <summary>"mention", "mentionInChat", "replyToReply", "reaction", "reactionInChat", "msGraph", ...</summary>
    public string ActivityType { get; set; } = "";

    /// <summary>For example "person", "everyone", "channel" (mentions), "like" (reactions), "privateMeetingUpdated". Can be empty.</summary>
    public string ActivitySubtype { get; set; } = "";

    /// <summary>AAD object id of the person (or "28:app:..." for an app) who caused the notification.</summary>
    public string SourceUserId { get; set; } = "";
    public string SourceUserName { get; set; } = "";

    /// <summary>Short text of the message it is about (plain text, can have line breaks). Can be cut by Teams.</summary>
    public string Preview { get; set; } = "";

    /// <summary>Room id of the message. Works as 'room_id' for teams_read_messages. Empty for meeting updates.</summary>
    public string SourceThreadId { get; set; } = "";

    /// <summary>Message id inside that room.</summary>
    public string SourceMessageId { get; set; } = "";

    /// <summary>Topic of the room, when it has one.</summary>
    public string SourceThreadTopic { get; set; } = "";

    /// <summary>True when the item is already read in Teams.</summary>
    public bool IsRead { get; set; }
}
