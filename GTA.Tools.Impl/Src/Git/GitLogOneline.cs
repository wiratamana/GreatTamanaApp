#if !CO_WORKER_USE
using System;

namespace GTA.Tools.Impl.Git
{
    public static partial class Git
    {
        /// <summary>Shows the commit history in a short, one-line-per-commit list (git log --oneline).</summary>
        public static string LogOneline(string repositoryPath)
        {
            try
            {
                return RunGitCommand(repositoryPath, "log", "--oneline");
            }
            catch (Exception ex)
            {
                return $"Error: failed to get one-line git log for '{repositoryPath}'. {ex.Message}";
            }
        }
    }
}
#endif
