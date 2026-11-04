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
            name: "git_stash_drop",
            description: "Delete a stash (git stash drop [stash]).")]
        [Parameter(index: 0, name: "repository_path",
            type: "string",
            description: "Full absolute Windows path of the local git repository.",
            required: true)]
        [Parameter(index: 1, name: "stash_ref",
            type: "string",
            description: "Optional specific stash to delete, e.g. \"stash@{1}\". Defaults to the most recent stash.",
            required: false)]
        public static object GitStashDrop(JsonObject input)
        {
            string? err = ResolveRepositoryPath(input, out string repositoryPath);
            if (err != null) return err;

            string? stashRef = GetOptionalString(input, "stash_ref");

            return Git.StashDrop(repositoryPath, stashRef);
        }
    }
}
#endif
