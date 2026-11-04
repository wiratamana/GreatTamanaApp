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
            name: "git_stash",
            description: "Temporarily hide unsaved work so something else can be worked on (git stash, " +
                         "or git stash push -m \"message\" when a message is supplied).")]
        [Parameter(index: 0, name: "repository_path",
            type: "string",
            description: "Full absolute Windows path of the local git repository.",
            required: true)]
        [Parameter(index: 1, name: "message",
            type: "string",
            description: "Optional description for this stash entry.",
            required: false)]
        public static object GitStash(JsonObject input)
        {
            string? err = ResolveRepositoryPath(input, out string repositoryPath);
            if (err != null) return err;

            string? message = GetOptionalString(input, "message");

            return Git.Stash(repositoryPath, message);
        }
    }
}
#endif
