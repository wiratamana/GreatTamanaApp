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
            name: "git_clone",
            description: "Clone a remote GitHub repository over HTTPS into a new local folder (git clone). " +
                         "Authentication is handled automatically: the GitHub Personal Access Token " +
                         "is loaded from Config/init.ini (override it for this one call via " +
                         "'use_custom_pat' if needed). This is an ONLINE operation, routed through the " +
                         "corporate proxy for this one invocation only — there is no Wi-Fi switching " +
                         "and no Windows proxy toggling involved. Never throws — errors come back as " +
                         "descriptive text.")]
        [Parameter(index: 0, name: "remote_url",
            type: "string",
            description: "HTTPS clone URL of the GitHub repository to clone, e.g. \"https://github.com/owner/repo.git\".",
            required: true)]
        [Parameter(index: 1, name: "destination_path",
            type: "string",
            description: "Full absolute Windows path of the local folder to clone into. Must not already exist, or " +
                         "if it does exist it must be empty — 'git clone' requires this. The parent folder is " +
                         "created automatically if missing.",
            required: true)]
        [Parameter(index: 2, name: "use_custom_pat",
            type: "string",
            description: "Optional. If supplied, this GitHub Personal Access Token OVERRIDES the one " +
                         "configured in Config/init.ini for this single call. Sent only as a one-shot " +
                         "'git -c http.extraheader' value for this single command — never written to " +
                         "disk, to the repository's git config, or embedded in the remote URL. If " +
                         "omitted, the token configured in Config/init.ini (github_pat) is used instead.",
            required: false)]
        [Parameter(index: 3, name: "branch",
            type: "string",
            description: "If supplied, adds --branch <branch> so only that branch's history (as the initial HEAD) is checked out.",
            required: false)]
        [Parameter(index: 4, name: "remote_name",
            type: "string",
            description: "If supplied, adds --origin <remote_name> so the cloned remote is named this instead of the default \"origin\".",
            required: false)]
        [Parameter(index: 5, name: "recurse_submodules",
            type: "boolean",
            description: "If true, adds --recurse-submodules so submodules are cloned/initialized too. Defaults to false.",
            required: false)]
        public static object GitClone(JsonObject input)
        {
            string? err = GetRequiredString(input, "remote_url", out string remoteUrl);
            if (err != null) return err;

            err = GetRequiredString(input, "destination_path", out string destinationPathRaw);
            if (err != null) return err;

            err = ResolveGithubToken(input, out string githubToken);
            if (err != null) return err;

            string? branch = GetOptionalString(input, "branch");
            string? remoteName = GetOptionalString(input, "remote_name");
            bool recurseSubmodules = ToolsIOHelpers.GetOptionalBool(input, "recurse_submodules", false);

            string destinationPath = ToolsIOHelpers.CleanPath(destinationPathRaw);

            return Git.Clone(remoteUrl, destinationPath, githubToken, branch, remoteName, recurseSubmodules);
        }
    }
}
#endif
