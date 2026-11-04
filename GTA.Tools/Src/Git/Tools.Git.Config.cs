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
            name: "git_config",
            description: "Set a git configuration value for a repository, e.g. your name or email " +
                         "(git config [--global] key value).")]
        [Parameter(index: 0, name: "repository_path",
            type: "string",
            description: "Full absolute Windows path of the local git repository (used as the working " +
                         "directory git runs from — required even when 'global' is true).",
            required: true)]
        [Parameter(index: 1, name: "key",
            type: "string",
            description: "Config key to set, e.g. \"user.name\" or \"user.email\".",
            required: true)]
        [Parameter(index: 2, name: "value",
            type: "string",
            description: "Value to assign to the config key, e.g. \"Your Name\".",
            required: true)]
        [Parameter(index: 3, name: "global",
            type: "boolean",
            description: "If true, sets the value globally (git config --global) instead of only for " +
                         "this repository. Defaults to false.",
            required: false)]
        public static object GitConfig(JsonObject input)
        {
            string? err = ResolveRepositoryPath(input, out string repositoryPath);
            if (err != null) return err;

            err = GetRequiredString(input, "key", out string key);
            if (err != null) return err;

            err = GetRequiredString(input, "value", out string value);
            if (err != null) return err;

            bool global = ToolsIOHelpers.GetOptionalBool(input, "global");

            return Git.Config(repositoryPath, key, value, global);
        }
    }
}
#endif
