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
            name: "git_tag",
            description: "Mark a specific point in history with a tag, e.g. a version like \"v1.0.0\" " +
                         "(git tag [tag-name]).")]
        [Parameter(index: 0, name: "repository_path",
            type: "string",
            description: "Full absolute Windows path of the local git repository.",
            required: true)]
        [Parameter(index: 1, name: "tag_name",
            type: "string",
            description: "Name of the tag to create at the current HEAD.",
            required: true)]
        public static object GitTag(JsonObject input)
        {
            string? err = ResolveRepositoryPath(input, out string repositoryPath);
            if (err != null) return err;

            err = GetRequiredString(input, "tag_name", out string tagName);
            if (err != null) return err;

            return Git.Tag(repositoryPath, tagName);
        }
    }
}
#endif
