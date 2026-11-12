using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GTA.Tools.Impl.Teams;

namespace GTA.Tools
{
    internal static partial class ToolsTeamsApi
    {
        // DANGEROUS tool: it sends a real message to real people and cannot be undone.
        //   [NotFindableByAi] -> not listed by get_available_tools, refused by load_tools, and ToolRegistry.TryExecute
        //                        refuses to run it unless the USER preloaded it (Preload Tools).
        //   [RecordToLog]     -> every call is written to the session log.
        [ToolTag("teams_api")]
        [NotFindableByAi]
        [RecordToLog]
        [ToolInfo(
            name: "teams_post_message",
            description:
                "SEND a message to a Microsoft Teams room, optionally with local files attached. " +
                "This really sends it, to real people, and it cannot be undone. " +
                "Only send what the user asked you to send, to the room the user named. " +
                "Files are uploaded to the user's OneDrive ('Microsoft Teams Chat Files') and attached as links. " +
                "Files can be attached to chats and group chats only, not to channels. " +
                "Files from the app's Config / Credentials folders and key files are refused. " +
                "Get 'room_id' from teams_fetch_room_id.")]
        [Parameter(index: 0, name: "room_id",
            type: "string",
            description: "Room id from teams_fetch_room_id. Example: '19:abc...@thread.v2'. " +
                         "'48:notes' is the user's own 'Notes to self' chat.",
            required: true)]
        [Parameter(index: 1, name: "text",
            type: "string",
            description: "Message text. How it is read depends on 'format'. " +
                         "Required unless 'attachments' is given.",
            required: false)]
        [Parameter(index: 3, name: "format",
            type: "string",
            description: "How 'text' is read: text | markdown | html. Default: text.\n" +
                         "text: literal characters, no formatting at all. Use it for plain messages.\n" +
                         "markdown: bold, italic, strikethrough, quote, nested bullets, numbered list, `inline code`, " +
                         "code block (keeps indentation), table, link, heading, horizontal line. " +
                         "No color, highlight, size or underline.\n" +
                         "html: everything in markdown, plus underline, sub, sup, strong, em, h1-h4. " +
                         "Color: <span style=\"color:#f00\"> (hex, rgb, named). Highlight: style background-color. " +
                         "Size: style font-size. Colors also work in code, code blocks and table cells. " +
                         "<div> background, <p> color / center work. &lt; &gt; &amp; are decoded.\n" +
                         "html does NOT work: <font color> (use span style), h5/h6 (look like normal text), " +
                         "big, ins, del, mark, kbd, cite, font-family, <details> (no collapse), div border, extra <br> gaps.\n" +
                         "Emoji and Japanese work in all formats.",
            required: false)]
        [Parameter(index: 2, name: "attachments",
            type: "string",
            description: "Optional. Full local file paths, separated by '|' (or one per line). " +
                         "At most 5 files, 100 MB each. Example: 'C:\\temp\\a.xlsx|C:\\temp\\b.pdf'.",
            required: false)]
        public static object TeamsPostMessage(JsonObject input)
        {
            string? roomId = input["room_id"]?.GetValue<string>()?.Trim();
            string text = input["text"]?.GetValue<string>() ?? "";
            string? attachmentsRaw = input["attachments"]?.GetValue<string>();

            if (string.IsNullOrWhiteSpace(roomId)) return "Error: 'room_id' is required.";

            string? formatRaw = input["format"]?.GetValue<string>()?.Trim();
            if (!Enum.TryParse(formatRaw ?? "text", ignoreCase: true, out TeamsTextFormat format) || !Enum.IsDefined(format))
                return $"Error: 'format' must be 'text', 'markdown' or 'html' (got '{formatRaw}').";

            List<string> files = SplitAttachmentPaths(attachmentsRaw);
            if (string.IsNullOrWhiteSpace(text) && files.Count == 0)
                return "Error: give 'text' or 'attachments' (nothing to send).";

            TeamsPostResult result;
            try
            {
                result = TeamsApi.PostMessage(roomId, text, files, format);
            }
            catch (Exception ex)
            {
                return FormatError($"teams_post_message {roomId} — NOT sent", ex);
            }

            var sb = new StringBuilder();
            sb.AppendLine($"✅ Message sent to room {roomId}" +
                          (result.MessageId.Length > 0 ? $" (message id {result.MessageId})" : ""));
            if (!string.IsNullOrWhiteSpace(text))
                sb.AppendLine($"Text: {text}");
            foreach (TeamsFileInfo file in result.UploadedFiles)
                sb.AppendLine($"📎 {file.Name} — {file.Url}");
            return sb.ToString().TrimEnd();
        }

        /// <summary>Accepts 'a|b', one path per line, or a JSON array text like ["a","b"].</summary>
        private static List<string> SplitAttachmentPaths(string? raw)
        {
            var paths = new List<string>();
            if (string.IsNullOrWhiteSpace(raw)) return paths;

            string trimmed = raw.Trim();
            if (trimmed.StartsWith('['))
            {
                try
                {
                    JsonNode? node = JsonNode.Parse(trimmed);
                    if (node is JsonArray arr)
                    {
                        foreach (JsonNode? item in arr)
                        {
                            string? p = item?.GetValue<string>();
                            if (!string.IsNullOrWhiteSpace(p)) paths.Add(p.Trim());
                        }
                        return paths;
                    }
                }
                catch (JsonException)
                {
                    // Not JSON after all (a path cannot start with '[' normally); use the plain split below.
                }
            }

            foreach (string part in trimmed.Split(new[] { '|', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string p = part.Trim().Trim('"');
                if (p.Length > 0) paths.Add(p);
            }
            return paths;
        }
    }
}
