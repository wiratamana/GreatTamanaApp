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
            name: "git_commit",
            description: "Save all currently staged changes with a descriptive message (git commit -m \"message\").")]
        [Parameter(index: 0, name: "repository_path",
            type: "string",
            description: "Full absolute Windows path of the local git repository.",
            required: true)]
        [Parameter(index: 1, name: "message",
            type: "string",
            description: "Commit message describing the change.",
            required: true)]
        public static object GitCommit(JsonObject input)
        {
            string? err = ResolveRepositoryPath(input, out string repositoryPath);
            if (err != null) return err;

            err = GetRequiredString(input, "message", out string message);
            if (err != null) return err;

            return Git.Commit(repositoryPath, message);
        }
    }
}
#endif
