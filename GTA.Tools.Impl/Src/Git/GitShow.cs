#if !CO_WORKER_USE
using System;

namespace GTA.Tools.Impl.Git
{
    public static partial class Git
    {
        /// <summary>Shows detailed information about one specific commit (git show [commit-hash]).</summary>
        public static string Show(string repositoryPath, string commitHash)
        {
            try
            {
                return RunGitCommand(repositoryPath, "show", commitHash);
            }
            catch (Exception ex)
            {
                return $"Error: failed to show commit '{commitHash}' in '{repositoryPath}'. {ex.Message}";
            }
        }
    }
}
#endif
