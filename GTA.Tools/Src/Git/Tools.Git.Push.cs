#if !CO_WORKER_USE
using System.Text.Json.Nodes;
using GTA.Tools.Impl.Git;

namespace GTA.Tools
{
    internal static partial class ToolsGit
    {
        [ToolTag("git")]
        [ToolInfo(
            name: "git_push",
            description: "Push local commits to a remote GitHub repository over HTTPS (git push). " +
                         "Authentication is handled automatically: the GitHub Personal Access Token " +
                         "is loaded from Config/init.ini (override it for this one call via " +
                         "'use_custom_pat' if needed). This is an ONLINE operation, routed through the " +
                         "corporate proxy for this one invocation only — there is no Wi-Fi switching " +
                         "and no Windows proxy toggling involved. Never throws — errors come back as " +
                         "descriptive text.")]
        [Parameter(index: 0, name: "repository_path",
            type: "string",
            description: "Full absolute Windows path of the local git repository to push from.",
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
            description: "Name of the remote to push to. Defaults to \"origin\".",
            required: false)]
        [Parameter(index: 3, name: "branch",
            type: "string",
            description: "Branch to push, e.g. \"main\". If omitted, pushes without an explicit refspec (the " +
                         "current branch's configured upstream).",
            required: false)]
        [Parameter(index: 4, name: "set_upstream",
            type: "boolean",
            description: "If true, adds --set-upstream (-u) so future plain 'git push'/'git pull' track this " +
                         "remote branch. Defaults to false.",
            required: false)]
        [Parameter(index: 5, name: "force",
            type: "boolean",
            description: "If true, adds --force. Use with caution — this can overwrite remote history. Defaults to false.",
            required: false)]
        public static object GitPush(JsonObject input)
        {
            string? err = ResolveRepositoryPath(input, out string repositoryPath);
            if (err != null) return err;

            err = ResolveGithubToken(input, out string githubToken);
            if (err != null) return err;

            string remoteName = GetOptionalString(input, "remote_name") ?? "origin";
            string? branch = GetOptionalString(input, "branch");
            bool setUpstream = ToolsIOHelpers.GetOptionalBool(input, "set_upstream", false);
            bool force = ToolsIOHelpers.GetOptionalBool(input, "force", false);

            return Git.Push(repositoryPath, githubToken, remoteName, branch, setUpstream, force);
        }
    }
}
#endif
