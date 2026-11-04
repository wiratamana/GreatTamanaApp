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
            name: "git_init",
            description: "Create a new, empty Git repository on the local disk (git init). " +
                         "The target folder is created first if it does not already exist. " +
                         "Safe to call again on an already-initialized repository.")]
        [Parameter(index: 0, name: "repository_path",
            type: "string",
            description: "Full absolute Windows path of the folder to initialize as a git repository.",
            required: true)]
        public static object GitInit(JsonObject input)
        {
            string? err = ResolveRepositoryPath(input, out string repositoryPath);
            if (err != null) return err;

            return Git.Init(repositoryPath);
        }
    }
}
#endif
