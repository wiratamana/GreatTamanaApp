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
            name: "git_add",
            description: "Stage a file (or, by default, all changed files in the repository) so it is " +
                         "ready to be committed (git add [file] / git add .).")]
        [Parameter(index: 0, name: "repository_path",
            type: "string",
            description: "Full absolute Windows path of the local git repository.",
            required: true)]
        [Parameter(index: 1, name: "pathspec",
            type: "string",
            description: "File/folder to stage, relative to the repository root. Defaults to \".\" (stage everything).",
            required: false)]
        public static object GitAdd(JsonObject input)
        {
            string? err = ResolveRepositoryPath(input, out string repositoryPath);
            if (err != null) return err;

            string pathspec = GetOptionalString(input, "pathspec") ?? ".";

            return Git.Add(repositoryPath, pathspec);
        }
    }
}
#endif
