#if !CO_WORKER_USE
using System;

namespace GTA.Tools.Impl.Git
{
    public static partial class Git
    {
        /// <summary>Combines another branch into the currently active branch (git merge [branch-name]).</summary>
        public static string Merge(string repositoryPath, string branchName)
        {
            try
            {
                return RunGitCommand(repositoryPath, "merge", branchName);
            }
            catch (Exception ex)
            {
                return $"Error: failed to merge branch '{branchName}' in '{repositoryPath}'. {ex.Message}";
            }
        }
    }
}
#endif
