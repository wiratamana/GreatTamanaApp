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
            name: "git_restore",
            description: "Discard unsaved local changes in a file, reverting it to its last committed " +
                         "state (git restore [file]).")]
        [Parameter(index: 0, name: "repository_path",
            type: "string",
            description: "Full absolute Windows path of the local git repository.",
            required: true)]
        [Parameter(index: 1, name: "file",
            type: "string",
            description: "File to restore, relative to the repository root.",
            required: true)]
        public static object GitRestore(JsonObject input)
        {
            string? err = ResolveRepositoryPath(input, out string repositoryPath);
            if (err != null) return err;

            err = GetRequiredString(input, "file", out string file);
            if (err != null) return err;

            return Git.Restore(repositoryPath, file);
        }
    }
}
#endif
