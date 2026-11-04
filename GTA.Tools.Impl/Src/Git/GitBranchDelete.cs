#if !CO_WORKER_USE
using System;

namespace GTA.Tools.Impl.Git
{
    public static partial class Git
    {
        /// <summary>Deletes a local branch (git branch -d [branch-name]).</summary>
        public static string BranchDelete(string repositoryPath, string branchName)
        {
            try
            {
                return RunGitCommand(repositoryPath, "branch", "-d", branchName);
            }
            catch (Exception ex)
            {
                return $"Error: failed to delete branch '{branchName}' in '{repositoryPath}'. {ex.Message}";
            }
        }
    }
}
#endif
