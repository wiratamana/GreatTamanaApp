#if !CO_WORKER_USE
using System;

namespace GTA.Tools.Impl.Git
{
    public static partial class Git
    {
        /// <summary>Creates a new branch and switches to it immediately (git switch -c [branch-name]).</summary>
        public static string SwitchCreate(string repositoryPath, string branchName)
        {
            try
            {
                return RunGitCommand(repositoryPath, "switch", "-c", branchName);
            }
            catch (Exception ex)
            {
                return $"Error: failed to create and switch to branch '{branchName}' in '{repositoryPath}'. {ex.Message}";
            }
        }
    }
}
#endif
