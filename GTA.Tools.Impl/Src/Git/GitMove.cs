#if !CO_WORKER_USE
using System;

namespace GTA.Tools.Impl.Git
{
    public static partial class Git
    {
        /// <summary>Renames or moves a tracked file (git mv [old-name] [new-name]).</summary>
        public static string Move(string repositoryPath, string oldPath, string newPath)
        {
            try
            {
                return RunGitCommand(repositoryPath, "mv", oldPath, newPath);
            }
            catch (Exception ex)
            {
                return $"Error: failed to move/rename '{oldPath}' to '{newPath}' in '{repositoryPath}'. {ex.Message}";
            }
        }
    }
}
#endif
