#if !CO_WORKER_USE
using System;

namespace GTA.Tools.Impl.Git
{
    public static partial class Git
    {
        /// <summary>Moves the current branch's commits on top of another branch for a cleaner history (git rebase [branch-name]).</summary>
        public static string Rebase(string repositoryPath, string branchName)
        {
            try
            {
                return RunGitCommand(repositoryPath, "rebase", branchName);
            }
            catch (Exception ex)
            {
                return $"Error: failed to rebase onto '{branchName}' in '{repositoryPath}'. {ex.Message}";
            }
        }
    }
}
#endif
