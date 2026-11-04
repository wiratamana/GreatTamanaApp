#if !CO_WORKER_USE
using System;

namespace GTA.Tools.Impl.Git
{
    public static partial class Git
    {
        /// <summary>Removes a file from both the working folder and git tracking (git rm [file]).</summary>
        public static string Remove(string repositoryPath, string file)
        {
            try
            {
                return RunGitCommand(repositoryPath, "rm", file);
            }
            catch (Exception ex)
            {
                return $"Error: failed to remove '{file}' in '{repositoryPath}'. {ex.Message}";
            }
        }
    }
}
#endif
