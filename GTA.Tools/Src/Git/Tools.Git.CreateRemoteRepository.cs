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
            name: "git_create_remote_repository",
            description: "Create a brand-new repository on GitHub via the GitHub REST API. Optionally also " +
                         "configures a LOCAL git repository's remote (git remote add/set-url) to point at the " +
                         "freshly created repo. Authentication is handled automatically: the GitHub Personal " +
                         "Access Token is loaded from Config/init.ini (override it for this one call via " +
                         "'use_custom_pat' if needed). This is an ONLINE operation, routed through the " +
                         "corporate proxy for this one invocation only — there is no Wi-Fi switching and no " +
                         "Windows proxy toggling involved. Never throws — errors come back as descriptive text.")]
        [Parameter(index: 0, name: "repository_name",
            type: "string",
            description: "Name of the new GitHub repository to create, e.g. \"my-new-project\".",
            required: true)]
        [Parameter(index: 1, name: "use_custom_pat",
            type: "string",
            description: "Optional. If supplied, this GitHub Personal Access Token OVERRIDES the one " +
                         "configured in Config/init.ini for this single call. Needs the \"repo\" scope " +
                         "(classic PAT) or \"Administration: write\" permission (fine-grained PAT). If " +
                         "omitted, the token configured in Config/init.ini (github_pat) is used instead.",
            required: false)]
        [Parameter(index: 2, name: "is_private",
            type: "boolean",
            description: "If true (default), the new repository is created as private. Set to false for a public repository.",
            required: false)]
        [Parameter(index: 3, name: "description",
            type: "string",
            description: "Optional repository description.",
            required: false)]
        [Parameter(index: 4, name: "organization",
            type: "string",
            description: "If supplied, the repository is created under this GitHub organization instead of the " +
                         "token owner's personal account.",
            required: false)]
        [Parameter(index: 5, name: "repository_path",
            type: "string",
            description: "Full absolute Windows path of a LOCAL git repository whose remote should be configured " +
                         "to point at the new repo. Required only when 'add_remote' is true (the default).",
            required: false)]
        [Parameter(index: 6, name: "remote_name",
            type: "string",
            description: "Name of the local remote to add/update. Defaults to \"origin\".",
            required: false)]
        [Parameter(index: 7, name: "add_remote",
            type: "boolean",
            description: "If true (default), after the GitHub repo is created, 'repository_path''s remote named " +
                         "'remote_name' is added (or updated if it already exists) to point at the new repo's " +
                         "clone URL. Set to false to only create the remote GitHub repository without touching " +
                         "any local folder (in which case 'repository_path' is not required).",
            required: false)]
        public static object GitCreateRemoteRepository(JsonObject input)
        {
            string? err = GetRequiredString(input, "repository_name", out string repositoryName);
            if (err != null) return err;

            err = ResolveGithubToken(input, out string githubToken);
            if (err != null) return err;

            bool isPrivate = ToolsIOHelpers.GetOptionalBool(input, "is_private", true);
            string? description = GetOptionalString(input, "description");
            string? organization = GetOptionalString(input, "organization");
            string? repositoryPath = GetOptionalString(input, "repository_path");
            string remoteName = GetOptionalString(input, "remote_name") ?? "origin";
            bool addRemote = ToolsIOHelpers.GetOptionalBool(input, "add_remote", true);

            if (addRemote && string.IsNullOrWhiteSpace(repositoryPath))
            {
                return "Error: 'repository_path' is required when 'add_remote' is true (the default) — either " +
                       "supply the local repository path to wire up, or pass add_remote=false to only create the " +
                       "remote GitHub repository.";
            }

            string cleanedRepositoryPath = string.IsNullOrWhiteSpace(repositoryPath)
                ? string.Empty
                : ToolsIOHelpers.CleanPath(repositoryPath);

            return Git.CreateRemoteRepository(
                githubToken,
                repositoryName,
                isPrivate,
                description,
                organization,
                addRemote ? cleanedRepositoryPath : null,
                remoteName,
                addRemote);
        }
    }
}
#endif
