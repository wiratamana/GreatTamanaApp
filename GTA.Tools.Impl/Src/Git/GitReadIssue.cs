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
        public static string ReadIssue(
            string repositoryPath,
            string githubToken,
            int issueNumber,
            string? repoOwner = null,
            string? repoName = null,
            string remoteName = "origin")
        {
            if (string.IsNullOrWhiteSpace(githubToken))
            {
                return "Error: 'githubToken' was null/empty — a GitHub Personal Access Token is required to read an issue via the GitHub API. Nothing was changed and no online operation was attempted.";
            }

            if (string.IsNullOrWhiteSpace(repositoryPath) || !Directory.Exists(repositoryPath))
            {
                return $"Error: repository path '{repositoryPath}' does not exist. Nothing was changed and no online operation was attempted.";
            }

            if (issueNumber <= 0)
            {
                return $"Error: 'issueNumber' must be a positive integer (got {issueNumber}). Nothing was changed and no online operation was attempted.";
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

                HttpResponseMessage response;
                string responseBody;
                try
                {
                    response = http.GetAsync(url).GetAwaiter().GetResult();
                    responseBody = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    return $"Error: HTTP request to the GitHub API failed (network/DNS/TLS/timeout issue) while reading issue #{issueNumber} in '{owner}/{repo}': {ex.GetType().Name}: {ex.Message}";
                }

                if (!response.IsSuccessStatusCode)
                {
                    return $"Error: GitHub API returned {(int)response.StatusCode} {response.ReasonPhrase} while reading issue #{issueNumber} in '{owner}/{repo}' at {url}. Response body: {responseBody}";
                }

                JsonNode? json;
                try
                {
                    json = JsonNode.Parse(responseBody);
                }
                catch (Exception ex)
                {
                    return $"Success (partially confirmed): GitHub API returned {(int)response.StatusCode} {response.ReasonPhrase} but the JSON response body could not be parsed ({ex.GetType().Name}: {ex.Message}). Raw response body: {responseBody}";
                }

                bool isPr = json?["pull_request"] != null;
                string title = json?["title"]?.GetValue<string>() ?? string.Empty;
                string state = json?["state"]?.GetValue<string>() ?? string.Empty;
                string user = json?["user"]?["login"]?.GetValue<string>() ?? string.Empty;
                string htmlUrl = json?["html_url"]?.GetValue<string>() ?? string.Empty;
                string body = json?["body"]?.GetValue<string>() ?? string.Empty;
                int commentCount = json?["comments"]?.GetValue<int>() ?? 0;
                string createdAt = json?["created_at"]?.GetValue<string>() ?? string.Empty;
                string updatedAt = json?["updated_at"]?.GetValue<string>() ?? string.Empty;

                var labels = new StringBuilder();
                if (json?["labels"] is JsonArray labelArr)
                {
                    foreach (JsonNode? label in labelArr)
                    {
                        string? name = label?["name"]?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            if (labels.Length > 0) labels.Append(", ");
                            labels.Append(name);
                        }
                    }
                }

                var sb = new StringBuilder();
                sb.AppendLine($"Success: issue #{issueNumber}{(isPr ? " (this is actually a PULL REQUEST, not a plain issue)" : "")} in '{owner}/{repo}':");
                sb.AppendLine($"Title: {title}");
                sb.AppendLine($"State: {state}");
                sb.AppendLine($"Author: {user}");
                sb.AppendLine($"Created: {createdAt}  |  Updated: {updatedAt}");
                sb.AppendLine($"Comments: {commentCount}");
                sb.AppendLine($"Labels: {(labels.Length > 0 ? labels.ToString() : "(none)")}");
                sb.AppendLine($"URL: {htmlUrl}");
                sb.AppendLine("Body:");
                sb.Append(string.IsNullOrEmpty(body) ? "(empty)" : body);

                return sb.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                return $"Error: unexpected exception while reading issue #{issueNumber} in '{owner}/{repo}': {ex.GetType().Name}: {ex.Message}";
            }
        }
    }
}
#endif
