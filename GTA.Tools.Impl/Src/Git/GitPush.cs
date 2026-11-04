#if !CO_WORKER_USE
using System.Collections.Generic;
using System.IO;

namespace GTA.Tools.Impl.Git
{
    public static partial class Git
    {
        public static string Push(
            string repositoryPath,
            string githubToken,
            string remoteName = "origin",
            string? branch = null,
            bool setUpstream = false,
            bool force = false)
        {
            if (string.IsNullOrWhiteSpace(githubToken))
            {
                return "Error: 'githubToken' was null/empty — a GitHub Personal Access Token is required to push over HTTPS. Nothing was changed and no online operation was attempted.";
            }

            if (string.IsNullOrWhiteSpace(repositoryPath) || !Directory.Exists(repositoryPath))
            {
                return $"Error: repository path '{repositoryPath}' does not exist. Nothing was changed and no online operation was attempted.";
            }

            if (string.IsNullOrWhiteSpace(remoteName))
            {
                remoteName = "origin";
            }

            var args = new List<string>(BuildTokenAuthArgs(githubToken));
            args.AddRange(BuildGitProxyArgs());
            args.Add("push");

            if (force) args.Add("--force");
            if (setUpstream) args.Add("--set-upstream");

            args.Add(remoteName);
            if (!string.IsNullOrWhiteSpace(branch))
            {
                args.Add(branch);
            }

            return RunGitCommand(repositoryPath, args.ToArray());
        }
    }
}
#endif
