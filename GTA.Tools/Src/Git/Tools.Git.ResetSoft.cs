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
            name: "git_reset_soft",
            description: "Undo the last commit(s) but keep the resulting code changes staged " +
                         "(git reset --soft HEAD~1).")]
        [Parameter(index: 0, name: "repository_path",
            type: "string",
            description: "Full absolute Windows path of the local git repository.",
            required: true)]
        [Parameter(index: 1, name: "commit_ref",
            type: "string",
            description: "Commit/ref to reset to. Defaults to \"HEAD~1\" (undo just the last commit).",
            required: false)]
        public static object GitResetSoft(JsonObject input)
        {
            string? err = ResolveRepositoryPath(input, out string repositoryPath);
            if (err != null) return err;

            string commitRef = GetOptionalString(input, "commit_ref") ?? "HEAD~1";

            return Git.ResetSoft(repositoryPath, commitRef);
        }
    }
}
#endif
