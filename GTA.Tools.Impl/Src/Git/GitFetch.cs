#if !CO_WORKER_USE
using System.Collections.Generic;
using System.IO;

namespace GTA.Tools.Impl.Git
{
    public static partial class Git
    {
        public static string Fetch(
            string repositoryPath,
            string githubToken,
            string remoteName = "origin",
            bool prune = false)
        {
            if (string.IsNullOrWhiteSpace(githubToken))
            {
                return "Error: 'githubToken' was null/empty — a GitHub Personal Access Token is required to fetch over HTTPS. Nothing was changed and no online operation was attempted.";
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
            args.Add("fetch");

            if (prune) args.Add("--prune");

            args.Add(remoteName);

            return RunGitCommand(repositoryPath, args.ToArray());
        }
    }
}
#endif
