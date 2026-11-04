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
            name: "git_fetch",
            description: "Fetch new refs/objects from a remote GitHub repository over HTTPS (git fetch). " +
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
            description: "Name of the remote to fetch from. Defaults to \"origin\".",
            required: false)]
        [Parameter(index: 3, name: "prune",
            type: "boolean",
            description: "If true, adds --prune (removes remote-tracking refs that no longer exist on the remote). Defaults to false.",
            required: false)]
        public static object GitFetch(JsonObject input)
        {
            string? err = ResolveRepositoryPath(input, out string repositoryPath);
            if (err != null) return err;

            err = ResolveGithubToken(input, out string githubToken);
            if (err != null) return err;

            string remoteName = GetOptionalString(input, "remote_name") ?? "origin";
            bool prune = ToolsIOHelpers.GetOptionalBool(input, "prune", false);

            return Git.Fetch(repositoryPath, githubToken, remoteName, prune);
        }
    }
}
#endif
