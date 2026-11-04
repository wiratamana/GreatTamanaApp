#if !CO_WORKER_USE
using System.Text.Json.Nodes;
using GTA.Core;

namespace GTA.Tools
{
    internal static partial class ToolsGit
    {
        internal static string? ResolveRepositoryPath(JsonObject input, out string repositoryPath)
        {
            repositoryPath = string.Empty;
            string? raw = input["repository_path"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(raw))
                return "Error: 'repository_path' parameter is missing or empty.";

            repositoryPath = ToolsIOHelpers.CleanPath(raw);
            return null;
        }

        internal static string? GetRequiredString(JsonObject input, string key, out string value)
        {
            value = string.Empty;
            string? raw = input[key]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(raw))
                return $"Error: '{key}' parameter is missing or empty.";

            value = raw;
            return null;
        }

        /// <summary>Resolve an optional string input. Returns <c>null</c> if the key is absent, null, or blank.</summary>
        internal static string? GetOptionalString(JsonObject input, string key)
        {
            string? raw = input[key]?.GetValue<string>();
            return string.IsNullOrWhiteSpace(raw) ? null : raw;
        }

        internal static string? GetRequiredInt(JsonObject input, string key, out int value)
        {
            if (!ToolsIOHelpers.TryGetInt(input[key], out value))
                return $"Error: '{key}' parameter is missing or not a valid integer.";

            return null;
        }

        internal static string? ResolveGithubToken(JsonObject input, out string githubToken)
        {
            string? customPat = GetOptionalString(input, "use_custom_pat");
            githubToken = !string.IsNullOrWhiteSpace(customPat) ? customPat : AppConfig.GITHUB_PAT;

            if (string.IsNullOrWhiteSpace(githubToken))
            {
                return "Error: no GitHub Personal Access Token is available — configure 'github_pat' " +
                       "in Config/init.ini, or pass 'use_custom_pat' to supply one for this single call.";
            }

            return null;
        }
    }
}
#endif
