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
        public static string SetIssueState(
            string repositoryPath,
            string githubToken,
            int issueNumber,
            string state,
            string? repoOwner = null,
            string? repoName = null,
            string remoteName = "origin")
        {
            if (string.IsNullOrWhiteSpace(githubToken))
            {
                return "Error: 'githubToken' was null/empty — a GitHub Personal Access Token is required to change an issue's state via the GitHub API. Nothing was changed and no online operation was attempted.";
            }

            if (string.IsNullOrWhiteSpace(repositoryPath) || !Directory.Exists(repositoryPath))
            {
                return $"Error: repository path '{repositoryPath}' does not exist. Nothing was changed and no online operation was attempted.";
            }

            if (issueNumber <= 0)
            {
                return $"Error: 'issueNumber' must be a positive integer (got {issueNumber}). Nothing was changed and no online operation was attempted.";
            }

            if (string.IsNullOrWhiteSpace(state) || (state != "open" && state != "closed"))
            {
                return $"Error: 'state' must be exactly \"open\" or \"closed\" (got \"{state}\"). Nothing was changed and no online operation was attempted.";
            }

            if (string.IsNullOrWhiteSpace(remoteName))
            {
                remoteName = "origin";
            }

            string? resolveErr = ResolveIssueOwnerRepo(repositoryPath, repoOwner, repoName, remoteName, out string owner, out string repo);
            if (resolveErr != null) return resolveErr;

            try
            {
                using var http = CreateGithubHttpClient(githubToken);

                string url = $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/issues/{issueNumber}";

                var payload = new JsonObject
                {
                    ["state"] = state
                };

                using var request = new HttpRequestMessage(HttpMethod.Patch, url)
                {
                    Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
                };

                HttpResponseMessage response;
                string responseBody;
                try
                {
                    response = http.SendAsync(request).GetAwaiter().GetResult();
                    responseBody = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    return $"Error: HTTP request to the GitHub API failed (network/DNS/TLS/timeout issue) while setting issue #{issueNumber} state to '{state}' in '{owner}/{repo}': {ex.GetType().Name}: {ex.Message}";
                }

                if (!response.IsSuccessStatusCode)
                {
                    return $"Error: GitHub API returned {(int)response.StatusCode} {response.ReasonPhrase} while setting issue #{issueNumber} state to '{state}' in '{owner}/{repo}' at {url}. Response body: {responseBody}";
                }

                string htmlUrl = string.Empty;
                string newState = string.Empty;
                try
                {
                    JsonNode? json = JsonNode.Parse(responseBody);
                    htmlUrl = json?["html_url"]?.GetValue<string>() ?? string.Empty;
                    newState = json?["state"]?.GetValue<string>() ?? string.Empty;
                }
                catch (Exception ex)
                {
                    return $"Success (partially confirmed): GitHub API returned {(int)response.StatusCode} {response.ReasonPhrase} (issue #{issueNumber} state likely changed to '{state}' in '{owner}/{repo}') but the JSON response body could not be parsed to extract state/html_url ({ex.GetType().Name}: {ex.Message}). Raw response body: {responseBody}";
                }

                return $"Success: issue #{issueNumber} in '{owner}/{repo}' state is now '{newState}'. URL: \"{htmlUrl}\".";
            }
            catch (Exception ex)
            {
                return $"Error: unexpected exception while setting issue #{issueNumber} state to '{state}' in '{owner}/{repo}': {ex.GetType().Name}: {ex.Message}";
            }
        }
    }
}
#endif
