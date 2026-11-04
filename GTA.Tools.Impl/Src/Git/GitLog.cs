#if !CO_WORKER_USE
using System;

namespace GTA.Tools.Impl.Git
{
    public static partial class Git
    {
        /// <summary>Shows the full history of all commits (git log).</summary>
        public static string Log(string repositoryPath)
        {
            try
            {
                return RunGitCommand(repositoryPath, "log");
            }
            catch (Exception ex)
            {
                return $"Error: failed to get git log for '{repositoryPath}'. {ex.Message}";
            }
        }
    }
}
#endif
