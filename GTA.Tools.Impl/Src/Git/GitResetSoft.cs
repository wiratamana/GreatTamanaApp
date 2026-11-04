#if !CO_WORKER_USE
using System;

namespace GTA.Tools.Impl.Git
{
    public static partial class Git
    {
        /// <summary>Undoes the last commit(s) but keeps the code changes staged (git reset --soft HEAD~1).</summary>
        public static string ResetSoft(string repositoryPath, string commitRef = "HEAD~1")
        {
            try
            {
                return RunGitCommand(repositoryPath, "reset", "--soft", commitRef);
            }
            catch (Exception ex)
            {
                return $"Error: failed to soft-reset to '{commitRef}' in '{repositoryPath}'. {ex.Message}";
            }
        }
    }
}
#endif
