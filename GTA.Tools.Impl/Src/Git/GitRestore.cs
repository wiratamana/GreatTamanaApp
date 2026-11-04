#if !CO_WORKER_USE
using System;

namespace GTA.Tools.Impl.Git
{
    public static partial class Git
    {
        /// <summary>Discards unsaved local changes in a file, reverting it to its last committed state (git restore [file]).</summary>
        public static string Restore(string repositoryPath, string file)
        {
            try
            {
                return RunGitCommand(repositoryPath, "restore", file);
            }
            catch (Exception ex)
            {
                return $"Error: failed to restore '{file}' in '{repositoryPath}'. {ex.Message}";
            }
        }
    }
}
#endif
