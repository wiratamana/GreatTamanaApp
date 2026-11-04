#if !CO_WORKER_USE
using System.Text.Json.Nodes;
using GTA.Tools.Impl.Git;

namespace GTA.Tools
{
    internal static partial class ToolsGit
    {
        [ToolTag("git")]
        [ToolInfo(
            name: "git_show",
            description: "Show detailed information about one specific commit (git show [commit-hash]).")]
        [Parameter(index: 0, name: "repository_path",
            type: "string",
            description: "Full absolute Windows path of the local git repository.",
            required: true)]
        [Parameter(index: 1, name: "commit_hash",
            type: "string",
            description: "Hash of the commit to inspect.",
            required: true)]
        public static object GitShow(JsonObject input)
        {
            string? err = ResolveRepositoryPath(input, out string repositoryPath);
            if (err != null) return err;

            err = GetRequiredString(input, "commit_hash", out string commitHash);
            if (err != null) return err;

            return Git.Show(repositoryPath, commitHash);
        }
    }
}
#endif
