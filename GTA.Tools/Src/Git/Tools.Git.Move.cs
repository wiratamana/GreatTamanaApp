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
            name: "git_mv",
            description: "Rename or move a tracked file (git mv [old-name] [new-name]).")]
        [Parameter(index: 0, name: "repository_path",
            type: "string",
            description: "Full absolute Windows path of the local git repository.",
            required: true)]
        [Parameter(index: 1, name: "old_path",
            type: "string",
            description: "Current file path, relative to the repository root.",
            required: true)]
        [Parameter(index: 2, name: "new_path",
            type: "string",
            description: "New file path, relative to the repository root.",
            required: true)]
        public static object GitMove(JsonObject input)
        {
            string? err = ResolveRepositoryPath(input, out string repositoryPath);
            if (err != null) return err;

            err = GetRequiredString(input, "old_path", out string oldPath);
            if (err != null) return err;

            err = GetRequiredString(input, "new_path", out string newPath);
            if (err != null) return err;

            return Git.Move(repositoryPath, oldPath, newPath);
        }
    }
}
#endif
