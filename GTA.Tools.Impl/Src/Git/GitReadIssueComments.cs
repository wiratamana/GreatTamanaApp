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
        public static string ReadIssueComments(
            string repositoryPath,
            string githubToken,
            int issueNumber,
            string? repoOwner = null,
            string? repoName = null,
            string remoteName = "origin",
            int perPage = 30,
            int page = 1)
        {
            if (string.IsNullOrWhiteSpace(githubToken))
            {
                return "Error: 'githubToken' was null/empty — a GitHub Personal Access Token is required to read issue comments via the GitHub API. Nothing was changed and no online operation was attempted.";
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

            if (perPage <= 0) perPage = 30;
            if (perPage > 100) perPage = 100;
            if (page <= 0) page = 1;

            string? resolveErr = ResolveIssueOwnerRepo(repositoryPath, repoOwner, repoName, remoteName, out string owner, out string repo);
            if (resolveErr != null) return resolveErr;

            try
            {
                using var http = CreateGithubHttpClient(githubToken);

                string url = $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/issues/{issueNumber}/comments" +
                             $"?per_page={perPage}&page={page}";

                HttpResponseMessage response;
                string responseBody;
                try
                {
                    response = http.GetAsync(url).GetAwaiter().GetResult();
                    responseBody = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    return $"Error: HTTP request to the GitHub API failed (network/DNS/TLS/timeout issue) while reading comments on issue #{issueNumber} in '{owner}/{repo}': {ex.GetType().Name}: {ex.Message}";
                }

                if (!response.IsSuccessStatusCode)
                {
                    return $"Error: GitHub API returned {(int)response.StatusCode} {response.ReasonPhrase} while reading comments on issue #{issueNumber} in '{owner}/{repo}' at {url}. Response body: {responseBody}";
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

                if (json is not JsonArray arr)
                {
                    return $"Success (partially confirmed): GitHub API returned {(int)response.StatusCode} {response.ReasonPhrase} but the response body was not a JSON array as expected. Raw response body: {responseBody}";
                }

                if (arr.Count == 0)
                {
                    return $"Success: issue #{issueNumber} in '{owner}/{repo}' has no comments (page {page}, per_page {perPage}).";
                }

                var sb = new StringBuilder();
                sb.AppendLine($"Success: {arr.Count} comment(s) on issue #{issueNumber} in '{owner}/{repo}' (page {page}, per_page {perPage}):");

                foreach (JsonNode? item in arr)
                {
                    long id = item?["id"]?.GetValue<long>() ?? 0;
                    string user = item?["user"]?["login"]?.GetValue<string>() ?? string.Empty;
                    string createdAt = item?["created_at"]?.GetValue<string>() ?? string.Empty;
                    string htmlUrl = item?["html_url"]?.GetValue<string>() ?? string.Empty;
                    string body = item?["body"]?.GetValue<string>() ?? string.Empty;

                    sb.AppendLine("---");
                    sb.AppendLine($"Comment id {id} by {user} at {createdAt} — {htmlUrl}");
                    sb.Append(string.IsNullOrEmpty(body) ? "(empty)" : body);
                    sb.AppendLine();
                }

                return sb.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                return $"Error: unexpected exception while reading comments on issue #{issueNumber} in '{owner}/{repo}': {ex.GetType().Name}: {ex.Message}";
            }
        }
    }
}
#endif
