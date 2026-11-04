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
            name: "git_clean",
            description: "PERMANENTLY DELETE all untracked files and folders in the repository " +
                         "(git clean -fd). Destructive and irreversible — only removes files git isn't tracking.")]
        [Parameter(index: 0, name: "repository_path",
            type: "string",
            description: "Full absolute Windows path of the local git repository.",
            required: true)]
        public static object GitClean(JsonObject input)
        {
            string? err = ResolveRepositoryPath(input, out string repositoryPath);
            if (err != null) return err;

            return Git.Clean(repositoryPath);
        }
    }
}
#endif
