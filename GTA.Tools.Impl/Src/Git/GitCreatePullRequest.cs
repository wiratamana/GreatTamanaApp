#if !CO_WORKER_USE
using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GTA.Tools.Impl.Git
{
    public static partial class Git
    {
        public static string CreatePullRequest(
            string repositoryPath,
            string githubToken,
            string headBranch,
            string baseBranch,
            string title,
            string? body = null,
            bool draft = false,
            bool maintainerCanModify = true,
            string? repoOwner = null,
            string? repoName = null,
            string remoteName = "origin")
        {
            if (string.IsNullOrWhiteSpace(githubToken))
            {
                return "Error: 'githubToken' was null/empty — a GitHub Personal Access Token is required to create a pull request via the GitHub API. Nothing was changed and no online operation was attempted.";
            }

            if (string.IsNullOrWhiteSpace(repositoryPath) || !Directory.Exists(repositoryPath))
            {
                return $"Error: repository path '{repositoryPath}' does not exist. Nothing was changed and no online operation was attempted.";
            }

            if (string.IsNullOrWhiteSpace(headBranch))
            {
                return "Error: 'headBranch' was null/empty — supply the branch containing the changes (e.g. \"feature-branch\", or \"fork-owner:branch\" for a cross-repository PR). Nothing was changed and no online operation was attempted.";
            }

            if (string.IsNullOrWhiteSpace(baseBranch))
            {
                return "Error: 'baseBranch' was null/empty — supply the branch the changes should be merged into (e.g. \"main\"). Nothing was changed and no online operation was attempted.";
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                return "Error: 'title' was null/empty — supply a pull request title. Nothing was changed and no online operation was attempted.";
            }

            if (string.IsNullOrWhiteSpace(remoteName))
            {
                remoteName = "origin";
            }

            string owner = repoOwner ?? string.Empty;
            string repo = repoName ?? string.Empty;

            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo))
            {
                if (!TryResolveOwnerRepoFromRemote(repositoryPath, remoteName, out string detectedOwner, out string detectedRepo, out string detectError))
                {
                    return $"Error: could not determine the GitHub owner/repository to open a pull request against ({detectError}). Either fix the local remote, or supply 'repoOwner'/'repoName' explicitly. Nothing was changed and no online operation was attempted.";
                }

                if (string.IsNullOrWhiteSpace(owner)) owner = detectedOwner;
                if (string.IsNullOrWhiteSpace(repo)) repo = detectedRepo;
            }

            try
            {
                using var http = CreateGithubHttpClient(githubToken);

                string url = $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/pulls";

                var payload = new JsonObject
                {
                    ["title"] = title,
                    ["head"] = headBranch,
                    ["base"] = baseBranch,
                    ["draft"] = draft,
                    ["maintainer_can_modify"] = maintainerCanModify
                };
                if (!string.IsNullOrWhiteSpace(body))
                {
                    payload["body"] = body;
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
                    return $"Error: HTTP request to the GitHub API failed (network/DNS/TLS/timeout issue) while creating a pull request in '{owner}/{repo}': {ex.GetType().Name}: {ex.Message}";
                }

                if (!response.IsSuccessStatusCode)
                {
                    return $"Error: GitHub API returned {(int)response.StatusCode} {response.ReasonPhrase} while creating a pull request in '{owner}/{repo}' ({headBranch} -> {baseBranch}) at {url}. Response body: {responseBody}";
                }

                string htmlUrl = string.Empty;
                int number = 0;
                try
                {
                    JsonNode? json = JsonNode.Parse(responseBody);
                    htmlUrl = json?["html_url"]?.GetValue<string>() ?? string.Empty;
                    number  = json?["number"]?.GetValue<int>() ?? 0;
                }
                catch (Exception ex)
                {
                    return $"Success (partially confirmed): GitHub API returned {(int)response.StatusCode} {response.ReasonPhrase} (pull request likely created in '{owner}/{repo}') but the JSON response body could not be parsed to extract html_url/number ({ex.GetType().Name}: {ex.Message}). Raw response body: {responseBody}";
                }

                return $"Success: pull request #{number} created in '{owner}/{repo}' ({headBranch} -> {baseBranch}). URL: \"{htmlUrl}\".";
            }
            catch (Exception ex)
            {
                return $"Error: unexpected exception while creating a pull request in '{owner}/{repo}': {ex.GetType().Name}: {ex.Message}";
            }
        }

        private static bool TryResolveOwnerRepoFromRemote(string repositoryPath, string remoteName, out string owner, out string repo, out string error)
        {
            owner = string.Empty;
            repo = string.Empty;
            error = string.Empty;

            string commandResult = RunGitCommand(repositoryPath, "remote", "get-url", remoteName);
            string? remoteUrl = ExtractGitStdout(commandResult);

            if (string.IsNullOrWhiteSpace(remoteUrl))
            {
                error = $"could not read remote '{remoteName}' URL in '{repositoryPath}': {commandResult}";
                return false;
            }

            Match match = Regex.Match(remoteUrl, @"github\.com[:/]+([^/]+)/(.+?)(?:\.git)?/?$", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                error = $"remote '{remoteName}' URL \"{remoteUrl}\" does not look like a github.com repository URL — cannot auto-detect owner/repo";
                return false;
            }

            owner = match.Groups[1].Value;
            repo = match.Groups[2].Value;
            return true;
        }

        private static string? ExtractGitStdout(string commandResult)
        {
            if (string.IsNullOrEmpty(commandResult))
            {
                return null;
            }

            const string stdoutMarker = "STDOUT:";
            int stdoutIdx = commandResult.IndexOf(stdoutMarker, StringComparison.Ordinal);
            if (stdoutIdx < 0)
            {
                return null;
            }

            string after = commandResult.Substring(stdoutIdx + stdoutMarker.Length);

            const string stderrMarker = "\nSTDERR:";
            int stderrIdx = after.IndexOf(stderrMarker, StringComparison.Ordinal);
            if (stderrIdx >= 0)
            {
                after = after.Substring(0, stderrIdx);
            }

            return after.Trim();
        }
    }
}
#endif
