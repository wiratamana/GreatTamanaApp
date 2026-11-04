#if !CO_WORKER_USE
using System;

namespace GTA.Tools.Impl.Git
{
    public static partial class Git
    {
        /// <summary>Switches to a different, already-existing branch (git switch [branch-name]).</summary>
        public static string Switch(string repositoryPath, string branchName)
        {
            try
            {
                return RunGitCommand(repositoryPath, "switch", branchName);
            }
            catch (Exception ex)
            {
                return $"Error: failed to switch to branch '{branchName}' in '{repositoryPath}'. {ex.Message}";
            }
        }
    }
}
#endif
