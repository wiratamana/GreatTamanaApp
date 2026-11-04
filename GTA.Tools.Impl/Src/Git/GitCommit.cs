#if !CO_WORKER_USE
using System;

namespace GTA.Tools.Impl.Git
{
    public static partial class Git
    {
        /// <summary>Saves all currently staged changes with a descriptive message (git commit -m "message").</summary>
        public static string Commit(string repositoryPath, string message)
        {
            try
            {
                return RunGitCommand(repositoryPath, "commit", "-m", message);
            }
            catch (Exception ex)
            {
                return $"Error: failed to commit in '{repositoryPath}'. {ex.Message}";
            }
        }
    }
}
#endif
