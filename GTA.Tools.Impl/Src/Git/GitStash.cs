#if !CO_WORKER_USE
using System;

namespace GTA.Tools.Impl.Git
{
    public static partial class Git
    {
        public static string Stash(string repositoryPath, string? message = null)
        {
            try
            {
                return string.IsNullOrWhiteSpace(message)
                    ? RunGitCommand(repositoryPath, "stash")
                    : RunGitCommand(repositoryPath, "stash", "push", "-m", message);
            }
            catch (Exception ex)
            {
                return $"Error: failed to stash changes in '{repositoryPath}'. {ex.Message}";
            }
        }
    }
}
#endif
