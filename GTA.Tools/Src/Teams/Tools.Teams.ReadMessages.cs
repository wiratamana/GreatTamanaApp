using System.Text;
using System.Text.Json.Nodes;
using GTA.Tools.Impl.Teams;

namespace GTA.Tools
{
    internal static partial class ToolsTeamsApi
    {
        private const int DefaultMaxMessages = 50;
        private const int HardMaxMessages = 500;

        [ToolTag("teams_api")]
        [ToolInfo(
            name: "teams_read_messages",
            description:
                "Read messages of one Microsoft Teams room. Returns the NEWEST messages inside the date range " +
                "(at most 'max_messages'), shown oldest first, with time, sender, plain text and attached file names. " +
                "Times are shown in this PC's time zone. Get the 'room_id' from teams_fetch_room_id. " +
                "Read only.")]
        [Parameter(index: 0, name: "room_id",
            type: "string",
            description: "Room id from teams_fetch_room_id. Example: '19:abc...@thread.v2'.",
            required: true)]
        [Parameter(index: 1, name: "date_from",
            type: "string",
            description: "Optional. Start of the range, this PC's time zone. 'yyyy-MM-dd' (start of that day) " +
                         "or 'yyyy-MM-dd HH:mm'. Default: no start limit.",
            required: false)]
        [Parameter(index: 2, name: "date_to",
            type: "string",
            description: "Optional. End of the range. 'yyyy-MM-dd' means the END of that day (included). " +
                         "Default: now.",
            required: false)]
        [Parameter(index: 3, name: "max_messages",
            type: "integer",
            description: "Maximum messages to return. Default: 50. Hard limit: 500.",
            required: false)]
        public static object TeamsReadMessages(JsonObject input)
        {
            string? roomId = input["room_id"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(roomId)) return "Error: 'room_id' is required.";

            if (!TryParseBoundary(input["date_from"]?.GetValue<string>(), isEnd: false, out DateTimeOffset? fromUtc, out string? fromError))
                return $"Error: 'date_from': {fromError}";
            if (!TryParseBoundary(input["date_to"]?.GetValue<string>(), isEnd: true, out DateTimeOffset? toUtc, out string? toError))
                return $"Error: 'date_to': {toError}";
            if (fromUtc.HasValue && toUtc.HasValue && fromUtc > toUtc)
                return "Error: 'date_from' is later than 'date_to'.";

            int max = Math.Clamp(input["max_messages"]?.GetValue<int>() ?? DefaultMaxMessages, 1, HardMaxMessages);

            List<TeamsMessage> messages;
            try
            {
                messages = TeamsApi.ReadMessages(roomId, fromUtc, toUtc, max);
            }
            catch (Exception ex)
            {
                return FormatError($"teams_read_messages {roomId}", ex);
            }

            var sb = new StringBuilder();
            sb.AppendLine($"✅ {messages.Count} message(s) in room {roomId} (time zone {LocalZoneLabel()})" +
                          (messages.Count >= max ? $" — limit of {max} reached, older messages in the range were not read" : ""));
            foreach (TeamsMessage m in messages)
            {
                string sender = m.SenderName.Length > 0 ? m.SenderName : m.SenderId;
                string text = m.Text.Replace("\n", "\n    ");
                sb.AppendLine($"[{LocalTime(m.TimeUtc)}] {sender} (id {m.Id}): {text}");
                foreach (TeamsFileInfo file in m.Files)
                    sb.AppendLine($"    📎 {file.Name}{(file.Url.Length > 0 ? $" — {file.Url}" : "")}");
            }
            return sb.ToString().TrimEnd();
        }
    }
}
