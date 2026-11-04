#if !CO_WORKER_USE
using System.Text.Json.Nodes;
using GTA.Tools.Impl.Git;

namespace GTA.Tools
{
    internal static partial class ToolsGit
    {
        [ToolTag("git")]
        [ToolInfo(
            name: "git_create_pull_request",
            description: "Open a new pull request on GitHub via the GitHub REST API (POST " +
                         "/repos/{owner}/{repo}/pulls). The target GitHub repository is auto-detected " +
                         "from the LOCAL repository's remote URL (see 'remote_name') unless BOTH " +
                         "'repo_owner' and 'repo_name' are supplied explicitly. Authentication is " +
                         "handled automatically: the GitHub Personal Access Token is loaded from " +
                         "Config/init.ini (override it for this one call via 'use_custom_pat' if " +
                         "needed). This is an ONLINE operation, routed through the corporate proxy for " +
                         "this one invocation only — there is no Wi-Fi switching and no Windows proxy " +
                         "toggling involved. Never throws — errors come back as descriptive text, e.g. " +
                         "a 403 if the token's account lacks push/PR permission on the repository, same " +
                         "as with git_push.")]
        [Parameter(index: 0, name: "repository_path",
            type: "string",
            description: "Full absolute Windows path of the local git repository. Used to auto-detect " +
                         "the GitHub owner/repo from its remote URL unless 'repo_owner'+'repo_name' are " +
                         "both supplied.",
            required: true)]
        [Parameter(index: 1, name: "head_branch",
            type: "string",
            description: "Branch containing the changes, e.g. \"feature-branch\". Must already exist on " +
                         "the remote (push it first with git_push). For a cross-repository (fork) pull " +
                         "request, use \"fork-owner:branch\".",
            required: true)]
        [Parameter(index: 2, name: "base_branch",
            type: "string",
            description: "Branch the changes should be merged into, e.g. \"main\".",
            required: true)]
        [Parameter(index: 3, name: "title",
            type: "string",
            description: "Pull request title.",
            required: true)]
        [Parameter(index: 4, name: "body",
            type: "string",
            description: "Optional pull request description/body (Markdown supported).",
            required: false)]
        [Parameter(index: 5, name: "draft",
            type: "boolean",
            description: "If true, opens the pull request as a draft. Defaults to false.",
            required: false)]
        [Parameter(index: 6, name: "maintainer_can_modify",
            type: "boolean",
            description: "If true (default), maintainers of the base repository are allowed to push to the head branch.",
            required: false)]
        [Parameter(index: 7, name: "repo_owner",
            type: "string",
            description: "Optional explicit GitHub owner/organization login. If either this or 'repo_name' " +
                         "is omitted, BOTH are auto-detected instead from the local repository's remote URL.",
            required: false)]
        [Parameter(index: 8, name: "repo_name",
            type: "string",
            description: "Optional explicit GitHub repository name. If either this or 'repo_owner' is " +
                         "omitted, BOTH are auto-detected instead from the local repository's remote URL.",
            required: false)]
        [Parameter(index: 9, name: "remote_name",
            type: "string",
            description: "Name of the local remote to inspect when auto-detecting owner/repo. Defaults " +
                         "to \"origin\". Ignored if both 'repo_owner' and 'repo_name' are supplied.",
            required: false)]
        [Parameter(index: 10, name: "use_custom_pat",
            type: "string",
            description: "Optional. If supplied, this GitHub Personal Access Token OVERRIDES the one " +
                         "configured in Config/init.ini for this single call. Needs the \"repo\" scope " +
                         "(classic PAT) or \"Pull requests: write\" permission (fine-grained PAT). If " +
                         "omitted, the token configured in Config/init.ini (github_pat) is used instead.",
            required: false)]
        public static object GitCreatePullRequest(JsonObject input)
        {
            string? err = ResolveRepositoryPath(input, out string repositoryPath);
            if (err != null) return err;

            err = GetRequiredString(input, "head_branch", out string headBranch);
            if (err != null) return err;

            err = GetRequiredString(input, "base_branch", out string baseBranch);
            if (err != null) return err;

            err = GetRequiredString(input, "title", out string title);
            if (err != null) return err;

            err = ResolveGithubToken(input, out string githubToken);
            if (err != null) return err;

            string? body = GetOptionalString(input, "body");
            bool draft = ToolsIOHelpers.GetOptionalBool(input, "draft", false);
            bool maintainerCanModify = ToolsIOHelpers.GetOptionalBool(input, "maintainer_can_modify", true);
            string? repoOwner = GetOptionalString(input, "repo_owner");
            string? repoName = GetOptionalString(input, "repo_name");
            string remoteName = GetOptionalString(input, "remote_name") ?? "origin";

            return Git.CreatePullRequest(
                repositoryPath,
                githubToken,
                headBranch,
                baseBranch,
                title,
                body,
                draft,
                maintainerCanModify,
                repoOwner,
                repoName,
                remoteName);
        }
    }
}
#endif
