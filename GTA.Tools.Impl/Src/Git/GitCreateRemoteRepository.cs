#if !CO_WORKER_USE
using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace GTA.Tools.Impl.Git
{
    public static partial class Git
    {
        public static string CreateRemoteRepository(
            string githubToken,
            string repositoryName,
            bool isPrivate = true,
            string? description = null,
            string? organization = null,
            string? localRepositoryPath = null,
            string remoteName = "origin",
            bool addRemote = true)
        {
            if (string.IsNullOrWhiteSpace(githubToken))
            {
                return "Error: 'githubToken' was null/empty — a GitHub Personal Access Token is required to create a repository via the GitHub API. Nothing was changed and no online operation was attempted.";
            }

            if (string.IsNullOrWhiteSpace(repositoryName))
            {
                return "Error: 'repositoryName' was null/empty — supply the name of the new GitHub repository to create. Nothing was changed and no online operation was attempted.";
            }

            if (string.IsNullOrWhiteSpace(remoteName))
            {
                remoteName = "origin";
            }

            if (addRemote && (string.IsNullOrWhiteSpace(localRepositoryPath) || !Directory.Exists(localRepositoryPath)))
            {
                return $"Error: 'addRemote' was true but local repository path '{localRepositoryPath}' does not exist. Either create/init the local repository first and pass its path, or pass addRemote=false to only create the remote GitHub repository without touching any local folder. Nothing was changed and no online operation was attempted.";
            }

            try
            {
                using var http = CreateGithubHttpClient(githubToken);

                string url = string.IsNullOrWhiteSpace(organization)
                    ? "https://api.github.com/user/repos"
                    : $"https://api.github.com/orgs/{Uri.EscapeDataString(organization)}/repos";

                var payload = new JsonObject
                {
                    ["name"]    = repositoryName,
                    ["private"] = isPrivate
                };
                if (!string.IsNullOrWhiteSpace(description))
                {
                    payload["description"] = description;
                }

                using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");

                HttpResponseMessage response;
                string responseBody;
                try
                {
                    response = http.PostAsync(url, content).GetAwaiter().GetResult();
                    responseBody = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    return $"Error: HTTP request to the GitHub API failed (network/DNS/TLS/timeout issue) while creating repository '{repositoryName}': {ex.GetType().Name}: {ex.Message}";
                }

                if (!response.IsSuccessStatusCode)
                {
                    return $"Error: GitHub API returned {(int)response.StatusCode} {response.ReasonPhrase} while creating repository '{repositoryName}' at {url}. Response body: {responseBody}";
                }

                string cloneUrl = string.Empty;
                string htmlUrl = string.Empty;
                try
                {
                    JsonNode? json = JsonNode.Parse(responseBody);
                    cloneUrl = json?["clone_url"]?.GetValue<string>() ?? string.Empty;
                    htmlUrl  = json?["html_url"]?.GetValue<string>() ?? string.Empty;
                }
                catch (Exception ex)
                {
                    return $"Success (partially confirmed): GitHub API returned {(int)response.StatusCode} {response.ReasonPhrase} (repository likely created) but the JSON response body could not be parsed to extract clone_url/html_url ({ex.GetType().Name}: {ex.Message}). Raw response body: {responseBody}";
                }

                var sb = new StringBuilder();
                sb.AppendLine($"Success: GitHub repository '{repositoryName}' created. HTML URL: \"{htmlUrl}\". Clone URL: \"{cloneUrl}\".");

                if (addRemote)
                {
                    if (string.IsNullOrWhiteSpace(cloneUrl))
                    {
                        sb.Append("Warning: the repository was created, but no clone_url was found in the GitHub API response, so the local remote was NOT configured.");
                    }
                    else
                    {
                        bool exists = RemoteExists(localRepositoryPath!, remoteName);
                        string remoteWireResult = exists
                            ? RunGitCommand(localRepositoryPath!, "remote", "set-url", remoteName, cloneUrl)
                            : RunGitCommand(localRepositoryPath!, "remote", "add", remoteName, cloneUrl);

                        sb.AppendLine($"Local remote '{remoteName}' in '{localRepositoryPath}' {(exists ? "updated (set-url)" : "added")} -> \"{cloneUrl}\":");
                        sb.Append(remoteWireResult);
                    }
                }

                return sb.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                return $"Error: unexpected exception while creating GitHub repository '{repositoryName}': {ex.GetType().Name}: {ex.Message}";
            }
        }
    }
}
#endif
