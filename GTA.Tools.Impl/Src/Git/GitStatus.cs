#if !CO_WORKER_USE
using System;

namespace GTA.Tools.Impl.Git
{
    public static partial class Git
    {
        /// <summary>Shows which files are changed, staged, or untracked in the repository (git status).</summary>
        public static string Status(string repositoryPath)
        {
            try
            {
                return RunGitCommand(repositoryPath, "status");
            }
            catch (Exception ex)
            {
                return $"Error: failed to get git status for '{repositoryPath}'. {ex.Message}";
            }
        }
    }
}
#endif
