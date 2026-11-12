using System.Text;
using System.Text.Json.Nodes;
using GTA.Tools.Impl.Teams;

namespace GTA.Tools
{
    internal static partial class ToolsTeamsApi
    {
        [ToolTag("teams_api")]
        [ToolInfo(
            name: "teams_fetch_user_name",
            description:
                "Look up people in the company directory (Microsoft Graph). Two ways: " +
                "(1) 'user_id' = the id you see in a Teams message sender or mention (a GUID, or '8:orgid:<GUID>', " +
                "or an e-mail / user principal name) -> returns that one person. " +
                "(2) 'query' = part of a name or e-mail -> returns up to 10 matches. " +
                "Gives display name, mail, user principal name, job title and department. Read only.")]
        [Parameter(index: 0, name: "user_id",
            type: "string",
            description: "User object id (GUID), '8:orgid:<GUID>', or e-mail / user principal name. " +
                         "Use this OR 'query'.",
            required: false)]
        [Parameter(index: 1, name: "query",
            type: "string",
            description: "Part of a display name, e-mail or user principal name. Use this OR 'user_id'.",
            required: false)]
        public static object TeamsFetchUserName(JsonObject input)
        {
            string? userId = input["user_id"]?.GetValue<string>()?.Trim();
            string? query = input["query"]?.GetValue<string>()?.Trim();

            if (string.IsNullOrEmpty(userId) && string.IsNullOrEmpty(query))
                return "Error: give 'user_id' or 'query'.";

            try
            {
                if (!string.IsNullOrEmpty(userId))
                {
                    TeamsUser? user = TeamsApi.FetchUser(userId);
                    return user == null
                        ? $"❌ No user found for '{userId}'."
                        : $"✅ 1 user\n{FormatUser(user)}";
                }

                List<TeamsUser> users = TeamsApi.SearchUsers(query!, 10);
                if (users.Count == 0) return $"❌ No user found for '{query}'.";

                var sb = new StringBuilder();
                sb.AppendLine($"✅ {users.Count} user(s) for '{query}'");
                foreach (TeamsUser user in users)
                    sb.AppendLine(FormatUser(user));
                return sb.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                return FormatError("teams_fetch_user_name", ex);
            }
        }

        private static string FormatUser(TeamsUser u)
        {
            var parts = new List<string> { $"{u.DisplayName} (id {u.Id})" };
            if (u.Mail.Length > 0) parts.Add($"mail {u.Mail}");
            if (u.UserPrincipalName.Length > 0 && u.UserPrincipalName != u.Mail) parts.Add($"upn {u.UserPrincipalName}");
            if (u.JobTitle.Length > 0) parts.Add($"title {u.JobTitle}");
            if (u.Department.Length > 0) parts.Add($"dept {u.Department}");
            return "- " + string.Join(" | ", parts);
        }
    }
}
