#if !CO_WORKER_USE
using System.Text.Json.Nodes;
using GTA.Tools.Impl.Git;

namespace GTA.Tools
{
    internal static partial class ToolsGit
    {
        [ToolTag("git")]
        [ToolInfo(
            name: "git_list_issues",
            description: "List issues in a GitHub repository via the GitHub REST API (GET " +
                         "/repos/{owner}/{repo}/issues). The target GitHub repository is " +
                         "auto-detected from the LOCAL repository's remote URL (see 'remote_name') " +
                         "unless BOTH 'repo_owner' and 'repo_name' are supplied explicitly. NOTE: " +
                         "GitHub's endpoint also returns pull requests (they are internally issues " +
                         "too) — these are excluded by default, set 'include_pull_requests' to true " +
                         "to include them (flagged \"(PR)\"). Authentication is handled automatically: " +
                         "the GitHub Personal Access Token is loaded from Config/init.ini (override it " +
                         "for this one call via 'use_custom_pat' if needed). This is an ONLINE " +
                         "operation, routed through the corporate proxy for this one invocation only " +
                         "— there is no Wi-Fi switching and no Windows proxy toggling involved. The " +
                         "returned text lists each issue's number/state/title/author/comment-count/" +
                         "last-updated-timestamp/URL (this never throws — errors come back as " +
                         "descriptive text).")]
        [Parameter(index: 0, name: "repository_path",
            type: "string",
            description: "Full absolute Windows path of the local git repository. Used to auto-detect " +
                         "the GitHub owner/repo from its remote URL unless 'repo_owner'+'repo_name' are " +
                         "both supplied.",
            required: true)]
        [Parameter(index: 1, name: "state",
            type: "string",
            description: "Filter by state: \"open\" (default), \"closed\", or \"all\".",
            required: false)]
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
        [Parameter(index: 7, name: "include_pull_requests",
            type: "boolean",
            description: "If true, pull requests (which GitHub also returns from this endpoint) are " +
                         "included in the listing, flagged \"(PR)\". Defaults to false (issues only).",
            required: false)]
        [Parameter(index: 8, name: "use_custom_pat",
            type: "string",
            description: "Optional. If supplied, this GitHub Personal Access Token OVERRIDES the one " +
                         "configured in Config/init.ini for this single call. If omitted, the token " +
                         "configured in Config/init.ini (github_pat) is used instead.",
            required: false)]
        public static object GitListIssues(JsonObject input)
        {
            string? err = ResolveRepositoryPath(input, out string repositoryPath);
            if (err != null) return err;

            err = ResolveGithubToken(input, out string githubToken);
            if (err != null) return err;

            string state = GetOptionalString(input, "state") ?? "open";
            string? repoOwner = GetOptionalString(input, "repo_owner");
            string? repoName = GetOptionalString(input, "repo_name");
            string remoteName = GetOptionalString(input, "remote_name") ?? "origin";
            int perPage = ToolsIOHelpers.GetOptionalInt(input["per_page"]) ?? 30;
            int page = ToolsIOHelpers.GetOptionalInt(input["page"]) ?? 1;
            bool includePullRequests = ToolsIOHelpers.GetOptionalBool(input, "include_pull_requests", false);

            return Git.ListIssues(
                repositoryPath,
                githubToken,
                state,
                repoOwner,
                repoName,
                remoteName,
                perPage,
                page,
                includePullRequests);
        }
    }
}
#endif
