#if !CO_WORKER_USE
using System;

namespace GTA.Tools.Impl.Git
{
    public static partial class Git
    {
        /// <summary>Shows exact line-by-line differences in unsaved (not-yet-staged) changes (git diff).</summary>
        public static string Diff(string repositoryPath)
        {
            try
            {
                return RunGitCommand(repositoryPath, "diff");
            }
            catch (Exception ex)
            {
                return $"Error: failed to get git diff for '{repositoryPath}'. {ex.Message}";
            }
        }
    }
}
#endif
