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
            name: "git_switch_create",
            description: "Create a new branch and switch to it immediately (git switch -c [branch-name]).")]
        [Parameter(index: 0, name: "repository_path",
            type: "string",
            description: "Full absolute Windows path of the local git repository.",
            required: true)]
        [Parameter(index: 1, name: "branch_name",
            type: "string",
            description: "Name of the new branch to create and switch to.",
            required: true)]
        public static object GitSwitchCreate(JsonObject input)
        {
            string? err = ResolveRepositoryPath(input, out string repositoryPath);
            if (err != null) return err;

            err = GetRequiredString(input, "branch_name", out string branchName);
            if (err != null) return err;

            return Git.SwitchCreate(repositoryPath, branchName);
        }
    }
}
#endif
