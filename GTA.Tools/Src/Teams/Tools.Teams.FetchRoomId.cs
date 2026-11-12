using System.Text;
using System.Text.Json.Nodes;
using GTA.Tools.Impl.Teams;

namespace GTA.Tools
{
    internal static partial class ToolsTeamsApi
    {
        [ToolTag("teams_api")]
        [ToolInfo(
            name: "teams_fetch_room_id",
            description:
                "List the Microsoft Teams rooms (chats, group chats, meeting chats) of the signed-in user, " +
                "with the room id and the room name. Use this first to find the 'room_id' that " +
                "teams_read_messages needs. For chats without a topic, the name is the list of the other members. " +
                "'Notes to self' (id 48:notes) is always in the list. " +
                "Read only. Sign-in uses the PrimaryAccount from init.ini.")]
        [Parameter(index: 0, name: "name_filter",
            type: "string",
            description: "Optional. Only rooms whose name contains this text (not case sensitive). Example: 'Ishizaki'.",
            required: false)]
        [Parameter(index: 1, name: "max_rooms",
            type: "integer",
            description: "Maximum rooms to return. Default: 200.",
            required: false)]
        public static object TeamsFetchRoomId(JsonObject input)
        {
            string? filter = input["name_filter"]?.GetValue<string>()?.Trim();
            int maxRooms = Math.Clamp(input["max_rooms"]?.GetValue<int>() ?? 200, 1, 1_000);

            List<TeamsRoom> rooms;
            try
            {
                rooms = TeamsApi.FetchRooms();
            }
            catch (Exception ex)
            {
                return FormatError("teams_fetch_room_id", ex);
            }

            if (!string.IsNullOrEmpty(filter))
                rooms = rooms.Where(r => r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

            var sb = new StringBuilder();
            sb.AppendLine($"✅ {rooms.Count} room(s){(string.IsNullOrEmpty(filter) ? "" : $" matching '{filter}'")}" +
                          (rooms.Count > maxRooms ? $" (showing the first {maxRooms})" : ""));
            sb.AppendLine("room_id | kind | name");
            foreach (TeamsRoom room in rooms.Take(maxRooms))
                sb.AppendLine($"{room.Id} | {room.Kind} | {room.Name}");
            return sb.ToString().TrimEnd();
        }
    }
}
