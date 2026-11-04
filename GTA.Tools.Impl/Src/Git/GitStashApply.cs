#if !CO_WORKER_USE
using System;

namespace GTA.Tools.Impl.Git
{
    public static partial class Git
    {
        /// <summary>Restores stashed work but keeps it stored in the stash (git stash apply [stash]).</summary>
        public static string StashApply(string repositoryPath, string? stashRef = null)
        {
            try
            {
                return string.IsNullOrWhiteSpace(stashRef)
                    ? RunGitCommand(repositoryPath, "stash", "apply")
                    : RunGitCommand(repositoryPath, "stash", "apply", stashRef);
            }
            catch (Exception ex)
            {
                return $"Error: failed to apply stash in '{repositoryPath}'. {ex.Message}";
            }
        }
    }
}
#endif
