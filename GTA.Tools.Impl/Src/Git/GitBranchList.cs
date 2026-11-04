#if !CO_WORKER_USE
using System;

namespace GTA.Tools.Impl.Git
{
    public static partial class Git
    {
        /// <summary>Lists all local branches (git branch).</summary>
        public static string BranchList(string repositoryPath)
        {
            try
            {
                return RunGitCommand(repositoryPath, "branch");
            }
            catch (Exception ex)
            {
                return $"Error: failed to list branches in '{repositoryPath}'. {ex.Message}";
            }
        }
    }
}
#endif
