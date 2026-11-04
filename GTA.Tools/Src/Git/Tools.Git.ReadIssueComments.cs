#if !CO_WORKER_USE
using System.Text.Json.Nodes;
using GTA.Tools.Impl.Git;

namespace GTA.Tools
{
    internal static partial class ToolsGit
    {
        [ToolTag("git")]
        [ToolInfo(
            name: "git_read_issue_comments",
            description: "Read all comments posted on a GitHub issue via the GitHub REST API (GET " +
                         "/repos/{owner}/{repo}/issues/{issue_number}/comments). The target GitHub " +
                         "repository is auto-detected from the LOCAL repository's remote URL (see " +
                         "'remote_name') unless BOTH 'repo_owner' and 'repo_name' are supplied " +
                         "explicitly. Authentication is handled automatically: the GitHub Personal " +
                         "Access Token is loaded from Config/init.ini (override it for this one call " +
                         "via 'use_custom_pat' if needed). This is an ONLINE operation, routed through " +
                         "the corporate proxy for this one invocation only — there is no Wi-Fi " +
                         "switching and no Windows proxy toggling involved. The returned text lists " +
                         "each comment's id/author/timestamp/URL/body (this never throws — errors come " +
                         "back as descriptive text).")]
        [Parameter(index: 0, name: "repository_path",
            type: "string",
            description: "Full absolute Windows path of the local git repository. Used to auto-detect " +
                         "the GitHub owner/repo from its remote URL unless 'repo_owner'+'repo_name' are " +
                         "both supplied.",
            required: true)]
        [Parameter(index: 1, name: "issue_number",
            type: "integer",
            description: "The issue number whose comments should be read (e.g. 42).",
            required: true)]
        [Parameter(index: 2, name: "repo_owner",
            type: "string",
            description: "Optional explicit GitHub owner/organization login. If either this or 'repo_name' " +
                         "is omitted, BOTH are auto-detected instead from the local repository's remote URL.",
            required: false)]
        [Parameter(index: 3, name: "repo_name",
            type: "string",
            description: "Optional explicit GitHub repository name. If either this or 'repo_owner' is " +
                         "omitted, BOTH are auto-detected instead from the local repository's remote URL.",
            required: false)]
        [Parameter(index: 4, name: "remote_name",
            type: "string",
            description: "Name of the local remote to inspect when auto-detecting owner/repo. Defaults " +
                         "to \"origin\". Ignored if both 'repo_owner' and 'repo_name' are supplied.",
            required: false)]
        [Parameter(index: 5, name: "per_page",
            type: "integer",
            description: "Results per page (GitHub max 100). Defaults to 30.",
            required: false)]
        [Parameter(index: 6, name: "page",
            type: "integer",
            description: "Page number to fetch (1-based). Defaults to 1.",
            required: false)]
        [Parameter(index: 7, name: "use_custom_pat",
            type: "string",
            description: "Optional. If supplied, this GitHub Personal Access Token OVERRIDES the one " +
                         "configured in Config/init.ini for this single call. If omitted, the token " +
                         "configured in Config/init.ini (github_pat) is used instead.",
            required: false)]
        public static object GitReadIssueComments(JsonObject input)
        {
            string? err = ResolveRepositoryPath(input, out string repositoryPath);
            if (err != null) return err;

            err = GetRequiredInt(input, "issue_number", out int issueNumber);
            if (err != null) return err;

            err = ResolveGithubToken(input, out string githubToken);
            if (err != null) return err;

            string? repoOwner = GetOptionalString(input, "repo_owner");
            string? repoName = GetOptionalString(input, "repo_name");
            string remoteName = GetOptionalString(input, "remote_name") ?? "origin";
            int perPage = ToolsIOHelpers.GetOptionalInt(input["per_page"]) ?? 30;
            int page = ToolsIOHelpers.GetOptionalInt(input["page"]) ?? 1;

            return Git.ReadIssueComments(
                repositoryPath,
                githubToken,
                issueNumber,
                repoOwner,
                repoName,
                remoteName,
                perPage,
                page);
        }
    }
}
#endif
