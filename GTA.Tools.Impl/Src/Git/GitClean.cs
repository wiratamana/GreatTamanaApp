#if !CO_WORKER_USE
using System;

namespace GTA.Tools.Impl.Git
{
    public static partial class Git
    {
        /// <summary>Permanently deletes all untracked files and folders in the repository (git clean -fd).</summary>
        public static string Clean(string repositoryPath)
        {
            try
            {
                return RunGitCommand(repositoryPath, "clean", "-fd");
            }
            catch (Exception ex)
            {
                return $"Error: failed to clean untracked files in '{repositoryPath}'. {ex.Message}";
            }
        }
    }
}
#endif
