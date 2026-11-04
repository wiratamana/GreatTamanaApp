#if !CO_WORKER_USE
using System;

namespace GTA.Tools.Impl.Git
{
    public static partial class Git
    {
        /// <summary>Views all saved stashes (git stash list).</summary>
        public static string StashList(string repositoryPath)
        {
            try
            {
                return RunGitCommand(repositoryPath, "stash", "list");
            }
            catch (Exception ex)
            {
                return $"Error: failed to list stashes in '{repositoryPath}'. {ex.Message}";
            }
        }
    }
}
#endif
