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
        public static string ListIssues(
            string repositoryPath,
            string githubToken,
            string state = "open",
            string? repoOwner = null,
            string? repoName = null,
            string remoteName = "origin",
            int perPage = 30,
            int page = 1,
            bool includePullRequests = false)
        {
            if (string.IsNullOrWhiteSpace(githubToken))
            {
                return "Error: 'githubToken' was null/empty — a GitHub Personal Access Token is required to list issues via the GitHub API. Nothing was changed and no online operation was attempted.";
            }

            if (string.IsNullOrWhiteSpace(repositoryPath) || !Directory.Exists(repositoryPath))
            {
                return $"Error: repository path '{repositoryPath}' does not exist. Nothing was changed and no online operation was attempted.";
            }

            if (string.IsNullOrWhiteSpace(state))
            {
                state = "open";
            }
            if (state != "open" && state != "closed" && state != "all")
            {
                return $"Error: 'state' must be \"open\", \"closed\", or \"all\" (got \"{state}\"). Nothing was changed and no online operation was attempted.";
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

                string url = $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/issues" +
                             $"?state={Uri.EscapeDataString(state)}&per_page={perPage}&page={page}";

                HttpResponseMessage response;
                string responseBody;
                try
                {
                    response = http.GetAsync(url).GetAwaiter().GetResult();
                    responseBody = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    return $"Error: HTTP request to the GitHub API failed (network/DNS/TLS/timeout issue) while listing issues in '{owner}/{repo}': {ex.GetType().Name}: {ex.Message}";
                }

                if (!response.IsSuccessStatusCode)
                {
                    return $"Error: GitHub API returned {(int)response.StatusCode} {response.ReasonPhrase} while listing issues in '{owner}/{repo}' at {url}. Response body: {responseBody}";
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

                var sb = new StringBuilder();
                int shown = 0;
                int skippedPr = 0;

                foreach (JsonNode? item in arr)
                {
                    bool isPr = item?["pull_request"] != null;
                    if (isPr && !includePullRequests)
                    {
                        skippedPr++;
                        continue;
                    }

                    int number = item?["number"]?.GetValue<int>() ?? 0;
                    string title = item?["title"]?.GetValue<string>() ?? string.Empty;
                    string itemState = item?["state"]?.GetValue<string>() ?? string.Empty;
                    string htmlUrl = item?["html_url"]?.GetValue<string>() ?? string.Empty;
                    string user = item?["user"]?["login"]?.GetValue<string>() ?? string.Empty;
                    int commentCount = item?["comments"]?.GetValue<int>() ?? 0;
                    string updatedAt = item?["updated_at"]?.GetValue<string>() ?? string.Empty;

                    sb.AppendLine($"#{number} [{itemState}]{(isPr ? " (PR)" : "")} \"{title}\" by {user} — {commentCount} comment(s) — updated {updatedAt} — {htmlUrl}");
                    shown++;
                }

                string prNote = skippedPr > 0
                    ? $", {skippedPr} pull request(s) excluded (pass include_pull_requests=true to include them)"
                    : string.Empty;

                if (shown == 0)
                {
                    return $"Success: no issues found in '{owner}/{repo}' matching state='{state}' (page {page}, per_page {perPage}){prNote}.";
                }

                return $"Success: {shown} issue(s) in '{owner}/{repo}' (state='{state}', page {page}, per_page {perPage}){prNote}:\n{sb.ToString().TrimEnd()}";
            }
            catch (Exception ex)
            {
                return $"Error: unexpected exception while listing issues in '{owner}/{repo}': {ex.GetType().Name}: {ex.Message}";
            }
        }
    }
}
#endif
