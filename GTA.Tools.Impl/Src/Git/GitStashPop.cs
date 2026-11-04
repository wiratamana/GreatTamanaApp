#if !CO_WORKER_USE
using System;

namespace GTA.Tools.Impl.Git
{
    public static partial class Git
    {
        /// <summary>Restores the most recent stashed work and removes it from stash storage (git stash pop).</summary>
        public static string StashPop(string repositoryPath)
        {
            try
            {
                return RunGitCommand(repositoryPath, "stash", "pop");
            }
            catch (Exception ex)
            {
                return $"Error: failed to pop stash in '{repositoryPath}'. {ex.Message}";
            }
        }
    }
}
#endif
