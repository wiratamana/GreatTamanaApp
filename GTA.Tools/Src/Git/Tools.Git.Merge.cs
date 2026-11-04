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
            name: "git_merge",
            description: "Combine another branch into the currently active branch (git merge [branch-name]).")]
        [Parameter(index: 0, name: "repository_path",
            type: "string",
            description: "Full absolute Windows path of the local git repository.",
            required: true)]
        [Parameter(index: 1, name: "branch_name",
            type: "string",
            description: "Name of the branch to merge into the current branch.",
            required: true)]
        public static object GitMerge(JsonObject input)
        {
            string? err = ResolveRepositoryPath(input, out string repositoryPath);
            if (err != null) return err;

            err = GetRequiredString(input, "branch_name", out string branchName);
            if (err != null) return err;

            return Git.Merge(repositoryPath, branchName);
        }
    }
}
#endif
