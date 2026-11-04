#if !CO_WORKER_USE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GTA.Tools.Impl.Git
{
    public static partial class Git
    {
        public static string Clone(
            string remoteUrl,
            string destinationPath,
            string githubToken,
            string? branch = null,
            string? remoteName = null,
            bool recurseSubmodules = false)
        {
            if (string.IsNullOrWhiteSpace(githubToken))
            {
                return "Error: 'githubToken' was null/empty — a GitHub Personal Access Token is required to clone over HTTPS. Nothing was changed and no online operation was attempted.";
            }

            if (string.IsNullOrWhiteSpace(remoteUrl))
            {
                return "Error: 'remoteUrl' was null/empty — supply the HTTPS clone URL of the GitHub repository to clone. Nothing was changed and no online operation was attempted.";
            }

            if (string.IsNullOrWhiteSpace(destinationPath))
            {
                return "Error: 'destinationPath' was null/empty — supply the full local folder path to clone into. Nothing was changed and no online operation was attempted.";
            }

            string fullDestinationPath;
            try
            {
                fullDestinationPath = Path.GetFullPath(destinationPath);
            }
            catch (Exception ex)
            {
                return $"Error: 'destinationPath' ('{destinationPath}') is not a valid path. {ex.Message}. Nothing was changed and no online operation was attempted.";
            }

            try
            {
                if (Directory.Exists(fullDestinationPath) && Directory.EnumerateFileSystemEntries(fullDestinationPath).Any())
                {
                    return $"Error: destination path '{fullDestinationPath}' already exists and is not empty. 'git clone' requires an empty or non-existent target folder. Nothing was changed and no online operation was attempted.";
                }
            }
            catch (Exception ex)
            {
                return $"Error: could not inspect destination path '{fullDestinationPath}'. {ex.Message}. Nothing was changed and no online operation was attempted.";
            }

            string trimmed = fullDestinationPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string? parentDir = Path.GetDirectoryName(trimmed);
            if (string.IsNullOrWhiteSpace(parentDir))
            {
                return $"Error: could not determine the parent folder of destination path '{fullDestinationPath}'. Nothing was changed and no online operation was attempted.";
            }

            try
            {
                if (!Directory.Exists(parentDir))
                {
                    Directory.CreateDirectory(parentDir);
                }
            }
            catch (Exception ex)
            {
                return $"Error: could not create parent folder '{parentDir}' for the clone destination. {ex.Message}. Nothing was changed and no online operation was attempted.";
            }

            var args = new List<string>(BuildTokenAuthArgs(githubToken));
            args.AddRange(BuildGitProxyArgs());
            args.Add("clone");

            if (!string.IsNullOrWhiteSpace(branch))
            {
                args.Add("--branch");
                args.Add(branch);
            }

            if (!string.IsNullOrWhiteSpace(remoteName))
            {
                args.Add("--origin");
                args.Add(remoteName);
            }

            if (recurseSubmodules)
            {
                args.Add("--recurse-submodules");
            }

            args.Add(remoteUrl);
            args.Add(fullDestinationPath);

            return RunGitCommand(parentDir, args.ToArray());
        }
    }
}
#endif
