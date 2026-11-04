#if !CO_WORKER_USE
using System;

namespace GTA.Tools.Impl.Git
{
    public static partial class Git
    {
        /// <summary>Undoes the last commit(s) and permanently deletes all resulting code changes (git reset --hard HEAD~1).</summary>
        public static string ResetHard(string repositoryPath, string commitRef = "HEAD~1")
        {
            try
            {
                return RunGitCommand(repositoryPath, "reset", "--hard", commitRef);
            }
            catch (Exception ex)
            {
                return $"Error: failed to hard-reset to '{commitRef}' in '{repositoryPath}'. {ex.Message}";
            }
        }
    }
}
#endif
