using System.Text;
using System.Text.Json.Nodes;
using GTA.Tools.Impl.Teams;

namespace GTA.Tools
{
    internal static partial class ToolsTeamsApi
    {
        // DANGEROUS tool: it changes a real message that real people can read ("edited" is shown), and the old text is lost.
        //   [NotFindableByAi] -> not listed by get_available_tools, refused by load_tools, and ToolRegistry.TryExecute
        //                        refuses to run it unless the USER preloaded it (Preload Tools).
        //   [RecordToLog]     -> every call is written to the session log.
        [ToolTag("teams_api")]
        [NotFindableByAi]
        [RecordToLog]
        [ToolInfo(
            name: "teams_edit_message",
            description:
                "EDIT the text of ONE existing message in a Microsoft Teams room. " +
                "The whole text is replaced. Everybody in the room sees the new text with an 'edited' mark, " +
                "and the old text is lost. Only edit the message the user named, with the text the user gave you. " +
                "Only the user's OWN messages can be edited. Messages with attached files are refused (the files could be lost). " +
                "@mentions are not kept: they become plain text. " +
                "Get 'room_id' from teams_fetch_room_id and 'message_id' from teams_read_messages (the 'id' of the line). " +
                "The tool checks the message first (exists, is yours, no files), edits it, then reads it back " +
                "and tells you the text before and after.")]
        [Parameter(index: 0, name: "room_id",
            type: "string",
            description: "Room id from teams_fetch_room_id. Example: '19:abc...@thread.v2'. " +
                         "'48:notes' is the user's own 'Notes to self' chat.",
            required: true)]
        [Parameter(index: 1, name: "message_id",
            type: "string",
            description: "Id of the message to edit: digits only, the 'id' shown by teams_read_messages. Example: '1793843144709'.",
            required: true)]
        [Parameter(index: 2, name: "text",
            type: "string",
            description: "The NEW full text of the message (it replaces the old text). How it is read depends on 'format'.",
            required: true)]
        [Parameter(index: 3, name: "format",
            type: "string",
            description: "How 'text' is read: text | markdown | html. Default: text. " +
                         "Same rules as teams_post_message: text = literal characters, no formatting; " +
                         "markdown = bold, italic, strikethrough, quote, lists, code, table, link, heading; " +
                         "html = markdown features plus underline, color (span style), size, sub, sup.",
            required: false)]
        public static object TeamsEditMessage(JsonObject input)
        {
            string? roomId = input["room_id"]?.GetValue<string>()?.Trim();
            string? messageId = input["message_id"]?.GetValue<string>()?.Trim();
            string text = input["text"]?.GetValue<string>() ?? "";

            if (string.IsNullOrWhiteSpace(roomId)) return "Error: 'room_id' is required.";
            if (string.IsNullOrWhiteSpace(messageId)) return "Error: 'message_id' is required (get it from teams_read_messages).";
            if (string.IsNullOrWhiteSpace(text)) return "Error: 'text' is required (an edit cannot make a message empty).";

            string? formatRaw = input["format"]?.GetValue<string>()?.Trim();
            if (!Enum.TryParse(formatRaw ?? "text", ignoreCase: true, out TeamsTextFormat format) || !Enum.IsDefined(format))
                return $"Error: 'format' must be 'text', 'markdown' or 'html' (got '{formatRaw}').";

            TeamsEditResult result;
            try
            {
                result = TeamsApi.EditMessage(roomId, messageId, text, format);
            }
            catch (Exception ex)
            {
                return FormatError($"teams_edit_message {roomId} / {messageId} — NOT changed (or not confirmed)", ex);
            }

            var sb = new StringBuilder();
            if (result.Verified)
                sb.AppendLine($"✅ Message {result.MessageId} in room {result.RoomId} was edited. Read-back confirms the new text.");
            else
                sb.AppendLine($"⚠️ Teams accepted the edit of message {result.MessageId} in room {result.RoomId}, " +
                              "but the read-back did not show the new text yet. Check with teams_read_messages.");

            sb.AppendLine($"Before: {result.PreviousText.Replace("\n", "\n        ")}");
            sb.AppendLine($"After : {(result.CurrentText.Length > 0 ? result.CurrentText : text).Replace("\n", "\n        ")}");
            return sb.ToString().TrimEnd();
        }
    }
}
