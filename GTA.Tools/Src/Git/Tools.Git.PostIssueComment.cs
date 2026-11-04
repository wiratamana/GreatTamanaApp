#if !CO_WORKER_USE
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using GTA.Tools.Impl.Git;

namespace GTA.Tools
{
    internal static partial class ToolsGit
    {
        [ToolTag("git")]
        [ToolInfo(
            name: "git_post_issue_comment",
            description: "Post a new comment on a GitHub issue via the GitHub REST API (POST " +
                         "/repos/{owner}/{repo}/issues/{issue_number}/comments), optionally with one " +
                         "or more IMAGES embedded inline (see 'image_paths') so they show up as real " +
                         "picture evidence on the GitHub web UI — e.g. a screenshot saved on disk by " +
                         "run_app_background/gte_send_request/load_image (look for a " +
                         "\"[[SAVED_IMAGE path=\\\"...\\\"]]\" marker in that tool's own result — that " +
                         "path is exactly what to pass here). The target GitHub repository is " +
                         "auto-detected from the LOCAL repository's remote URL (see " +
                         "'remote_name') unless BOTH 'repo_owner' and 'repo_name' are supplied " +
                         "explicitly. Authentication is handled automatically: the GitHub Personal " +
                         "Access Token is loaded from Config/init.ini (override it for this one call " +
                         "via 'use_custom_pat' if needed). This is an ONLINE operation, routed through " +
                         "the corporate proxy for this one invocation only — there is no Wi-Fi " +
                         "switching and no Windows proxy toggling involved (this never throws — errors " +
                         "come back as descriptive text).")]
        [Parameter(index: 0, name: "repository_path",
            type: "string",
            description: "Full absolute Windows path of the local git repository. Used to auto-detect " +
                         "the GitHub owner/repo from its remote URL unless 'repo_owner'+'repo_name' are " +
                         "both supplied.",
            required: true)]
        [Parameter(index: 1, name: "issue_number",
            type: "integer",
            description: "The issue number to comment on (e.g. 42).",
            required: true)]
        [Parameter(index: 2, name: "comment_body",
            type: "string",
            description: "The comment text to post (Markdown supported). If 'image_paths' is also " +
                         "supplied, each image is appended/embedded AFTER this text automatically — " +
                         "do not manually add Markdown image syntax for them yourself.",
            required: true)]
        [Parameter(index: 3, name: "repo_owner",
            type: "string",
            description: "Optional explicit GitHub owner/organization login. If either this or 'repo_name' " +
                         "is omitted, BOTH are auto-detected instead from the local repository's remote URL.",
            required: false)]
        [Parameter(index: 4, name: "repo_name",
            type: "string",
            description: "Optional explicit GitHub repository name. If either this or 'repo_owner' is " +
                         "omitted, BOTH are auto-detected instead from the local repository's remote URL.",
            required: false)]
        [Parameter(index: 5, name: "remote_name",
            type: "string",
            description: "Name of the local remote to inspect when auto-detecting owner/repo. Defaults " +
                         "to \"origin\". Ignored if both 'repo_owner' and 'repo_name' are supplied.",
            required: false)]
        [Parameter(index: 6, name: "use_custom_pat",
            type: "string",
            description: "Optional. If supplied, this GitHub Personal Access Token OVERRIDES the one " +
                         "configured in Config/init.ini for this single call. If omitted, the token " +
                         "configured in Config/init.ini (github_pat) is used instead.",
            required: false)]
        [Parameter(index: 7, name: "image_paths",
            type: "string",
            description: "Optional. Comma-separated FULL ABSOLUTE Windows path(s) of local image file(s) " +
                         "(png/jpg/jpeg/gif/webp) to attach, e.g. \"C:\\...\\evidence\\img_ab12.png\" or " +
                         "\"C:\\shot1.png,C:\\shot2.png\". Each is uploaded to the repository (via the " +
                         "GitHub Contents API — committed under an auto-generated " +
                         "'issue-attachments/{issue_number}/…' path on the default branch, since GitHub " +
                         "has no separate 'attach file to comment' endpoint) and embedded inline in the " +
                         "comment as a Markdown image, so it renders as a real picture on the GitHub web " +
                         "UI exactly like a normal drag-dropped screenshot. Any single image that fails " +
                         "to upload does NOT abort the whole comment — a short \"⚠ could not attach…\" " +
                         "note takes its place instead. Omit entirely for a plain text-only comment.",
            required: false)]
        public static object GitPostIssueComment(JsonObject input)
        {
            string? err = ResolveRepositoryPath(input, out string repositoryPath);
            if (err != null) return err;

            err = GetRequiredInt(input, "issue_number", out int issueNumber);
            if (err != null) return err;

            err = GetRequiredString(input, "comment_body", out string commentBody);
            if (err != null) return err;

            err = ResolveGithubToken(input, out string githubToken);
            if (err != null) return err;

            string? repoOwner = GetOptionalString(input, "repo_owner");
            string? repoName = GetOptionalString(input, "repo_name");
            string remoteName = GetOptionalString(input, "remote_name") ?? "origin";

            string? imagePathsRaw = GetOptionalString(input, "image_paths");
            List<string>? imagePaths = string.IsNullOrWhiteSpace(imagePathsRaw)
                ? null
                : imagePathsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(p => p.Length > 0)
                    .ToList();

            return Git.PostIssueComment(
                repositoryPath,
                githubToken,
                issueNumber,
                commentBody,
                repoOwner,
                repoName,
                remoteName,
                imagePaths);
        }
    }
}
#endif
