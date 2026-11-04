#if !CO_WORKER_USE
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace GTA.Tools.Impl.Git
{
    public static partial class Git
    {
        public static string PostIssueComment(
            string repositoryPath,
            string githubToken,
            int issueNumber,
            string commentBody,
            string? repoOwner = null,
            string? repoName = null,
            string remoteName = "origin",
            List<string>? imagePaths = null)
        {
            if (string.IsNullOrWhiteSpace(githubToken))
            {
                return "Error: 'githubToken' was null/empty — a GitHub Personal Access Token is required to post an issue comment via the GitHub API. Nothing was changed and no online operation was attempted.";
            }

            if (string.IsNullOrWhiteSpace(repositoryPath) || !Directory.Exists(repositoryPath))
            {
                return $"Error: repository path '{repositoryPath}' does not exist. Nothing was changed and no online operation was attempted.";
            }

            if (issueNumber <= 0)
            {
                return $"Error: 'issueNumber' must be a positive integer (got {issueNumber}). Nothing was changed and no online operation was attempted.";
            }

            if (string.IsNullOrWhiteSpace(commentBody))
            {
                return "Error: 'commentBody' was null/empty — supply the comment text to post. Nothing was changed and no online operation was attempted.";
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

                string finalBody = commentBody;
                int imagesAttached = 0;

                if (imagePaths != null && imagePaths.Count > 0)
                {
                    var gallery = new StringBuilder();
                    foreach (string imgPath in imagePaths)
                    {
                        string? downloadUrl = TryUploadIssueImageAsset(http, owner, repo, issueNumber, imgPath, out string? uploadErr);
                        gallery.AppendLine();
                        if (downloadUrl != null)
                        {
                            gallery.AppendLine($"![{Path.GetFileName(imgPath)}]({downloadUrl})");
                            imagesAttached++;
                        }
                        else
                        {
                            gallery.AppendLine($"_(⚠ could not attach image \"{imgPath}\": {uploadErr})_");
                        }
                    }
                    finalBody = commentBody + "\n" + gallery;
                }

                string url = $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/issues/{issueNumber}/comments";

                var payload = new JsonObject
                {
                    ["body"] = finalBody
                };

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
                    return $"Error: HTTP request to the GitHub API failed (network/DNS/TLS/timeout issue) while posting a comment on issue #{issueNumber} in '{owner}/{repo}': {ex.GetType().Name}: {ex.Message}";
                }

                if (!response.IsSuccessStatusCode)
                {
                    return $"Error: GitHub API returned {(int)response.StatusCode} {response.ReasonPhrase} while posting a comment on issue #{issueNumber} in '{owner}/{repo}' at {url}. Response body: {responseBody}";
                }

                string htmlUrl = string.Empty;
                long commentId = 0;
                try
                {
                    JsonNode? json = JsonNode.Parse(responseBody);
                    htmlUrl = json?["html_url"]?.GetValue<string>() ?? string.Empty;
                    commentId = json?["id"]?.GetValue<long>() ?? 0;
                }
                catch (Exception ex)
                {
                    return $"Success (partially confirmed): GitHub API returned {(int)response.StatusCode} {response.ReasonPhrase} (comment likely posted on issue #{issueNumber} in '{owner}/{repo}') but the JSON response body could not be parsed to extract html_url/id ({ex.GetType().Name}: {ex.Message}). Raw response body: {responseBody}";
                }

                string imageNote = imagePaths != null && imagePaths.Count > 0
                    ? $", {imagesAttached}/{imagePaths.Count} image(s) attached"
                    : string.Empty;

                return $"Success: comment id {commentId} posted on issue #{issueNumber} in '{owner}/{repo}'{imageNote}. URL: \"{htmlUrl}\".";
            }
            catch (Exception ex)
            {
                return $"Error: unexpected exception while posting a comment on issue #{issueNumber} in '{owner}/{repo}': {ex.GetType().Name}: {ex.Message}";
            }
        }

        private static string? TryUploadIssueImageAsset(
            HttpClient http, string owner, string repo, int issueNumber, string localImagePath, out string? errorMessage)
        {
            errorMessage = null;

            if (string.IsNullOrWhiteSpace(localImagePath) || !File.Exists(localImagePath))
            {
                errorMessage = $"file not found: '{localImagePath}'";
                return null;
            }

            string ext = Path.GetExtension(localImagePath).TrimStart('.').ToLowerInvariant();
            if (ext != "png" && ext != "jpg" && ext != "jpeg" && ext != "gif" && ext != "webp")
            {
                errorMessage = $"unsupported image extension '.{ext}' (supported: png, jpg, jpeg, gif, webp)";
                return null;
            }

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(localImagePath);
            }
            catch (Exception ex)
            {
                errorMessage = $"could not read local file: {ex.GetType().Name}: {ex.Message}";
                return null;
            }

            string safeName = SanitizeAssetFileNamePart(Path.GetFileNameWithoutExtension(localImagePath));
            string uploadPath = $"issue-attachments/{issueNumber}/{DateTimeOffset.UtcNow:yyyyMMdd_HHmmssfff}_{safeName}.{ext}";
            string url = $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/contents/{uploadPath}";

            var payload = new JsonObject
            {
                ["message"] = $"Attach image evidence for issue #{issueNumber} (via git_post_issue_comment)",
                ["content"] = Convert.ToBase64String(bytes),
            };

            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            string responseBody;
            try
            {
                response = http.PutAsync(url, content).GetAwaiter().GetResult();
                responseBody = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                errorMessage = $"HTTP request failed while uploading image asset: {ex.GetType().Name}: {ex.Message}";
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                errorMessage = $"GitHub API returned {(int)response.StatusCode} {response.ReasonPhrase} while uploading image asset. Response: {responseBody}";
                return null;
            }

            try
            {
                JsonNode? json = JsonNode.Parse(responseBody);
                string? downloadUrl = json?["content"]?["download_url"]?.GetValue<string>();
                if (string.IsNullOrEmpty(downloadUrl))
                {
                    errorMessage = "upload succeeded but no 'download_url' was returned in the response.";
                    return null;
                }
                return downloadUrl;
            }
            catch (Exception ex)
            {
                errorMessage = $"upload succeeded but the response JSON could not be parsed: {ex.GetType().Name}: {ex.Message}";
                return null;
            }
        }

        private static string SanitizeAssetFileNamePart(string name)
        {
            var sb = new StringBuilder();
            foreach (char c in name)
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            return sb.Length > 0 ? sb.ToString() : "image";
        }
    }
}
#endif
