#if !CO_WORKER_USE
using System;

namespace GTA.Tools.Impl.Git
{
    public static partial class Git
    {
        /// <summary>Deletes a stash (git stash drop [stash]).</summary>
        public static string StashDrop(string repositoryPath, string? stashRef = null)
        {
            try
            {
                return string.IsNullOrWhiteSpace(stashRef)
                    ? RunGitCommand(repositoryPath, "stash", "drop")
                    : RunGitCommand(repositoryPath, "stash", "drop", stashRef);
            }
            catch (Exception ex)
            {
                return $"Error: failed to drop stash in '{repositoryPath}'. {ex.Message}";
            }
        }
    }
}
#endif
