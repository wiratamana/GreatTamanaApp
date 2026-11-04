#if !CO_WORKER_USE
using System.Text.Json.Nodes;
using GTA.Tools.Impl.Git;

namespace GTA.Tools
{
    internal static partial class ToolsGit
    {
        [ToolTag("git")]
        [FileMutating]
        [ToolInfo(
            name: "git_pull",
            description: "Pull (fetch + merge/rebase) from a remote GitHub repository over HTTPS (git pull). " +
                         "Authentication is handled automatically: the GitHub Personal Access Token " +
                         "is loaded from Config/init.ini (override it for this one call via " +
                         "'use_custom_pat' if needed). This is an ONLINE operation, routed through the " +
                         "corporate proxy for this one invocation only — there is no Wi-Fi switching " +
                         "and no Windows proxy toggling involved. Never throws — errors come back as " +
                         "descriptive text.")]
        [Parameter(index: 0, name: "repository_path",
            type: "string",
            description: "Full absolute Windows path of the local git repository.",
            required: true)]
        [Parameter(index: 1, name: "use_custom_pat",
            type: "string",
            description: "Optional. If supplied, this GitHub Personal Access Token OVERRIDES the one " +
                         "configured in Config/init.ini for this single call. Sent only as a one-shot " +
                         "'git -c http.extraheader' value for this single command — never written to " +
                         "disk, to the repository's git config, or embedded in the remote URL. If " +
                         "omitted, the token configured in Config/init.ini (github_pat) is used instead.",
            required: false)]
        [Parameter(index: 2, name: "remote_name",
            type: "string",
            description: "Name of the remote to pull from. Defaults to \"origin\".",
            required: false)]
        [Parameter(index: 3, name: "branch",
            type: "string",
            description: "Branch to pull, e.g. \"main\". If omitted, pulls the current branch's configured upstream.",
            required: false)]
        [Parameter(index: 4, name: "rebase",
            type: "boolean",
            description: "If true, adds --rebase instead of the default merge. Defaults to false.",
            required: false)]
        public static object GitPull(JsonObject input)
        {
            string? err = ResolveRepositoryPath(input, out string repositoryPath);
            if (err != null) return err;

            err = ResolveGithubToken(input, out string githubToken);
            if (err != null) return err;

            string remoteName = GetOptionalString(input, "remote_name") ?? "origin";
            string? branch = GetOptionalString(input, "branch");
            bool rebase = ToolsIOHelpers.GetOptionalBool(input, "rebase", false);

            return Git.Pull(repositoryPath, githubToken, remoteName, branch, rebase);
        }
    }
}
#endif
