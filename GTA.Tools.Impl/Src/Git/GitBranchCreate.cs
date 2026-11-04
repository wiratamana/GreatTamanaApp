#if !CO_WORKER_USE
using System;

namespace GTA.Tools.Impl.Git
{
    public static partial class Git
    {
        /// <summary>Creates a new local branch without switching to it (git branch [branch-name]).</summary>
        public static string BranchCreate(string repositoryPath, string branchName)
        {
            try
            {
                return RunGitCommand(repositoryPath, "branch", branchName);
            }
            catch (Exception ex)
            {
                return $"Error: failed to create branch '{branchName}' in '{repositoryPath}'. {ex.Message}";
            }
        }
    }
}
#endif
