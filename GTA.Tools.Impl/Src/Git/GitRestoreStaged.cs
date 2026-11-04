#if !CO_WORKER_USE
using System;

namespace GTA.Tools.Impl.Git
{
    public static partial class Git
    {
        /// <summary>Unstages a file without discarding its code changes (git restore --staged [file]).</summary>
        public static string RestoreStaged(string repositoryPath, string file)
        {
            try
            {
                return RunGitCommand(repositoryPath, "restore", "--staged", file);
            }
            catch (Exception ex)
            {
                return $"Error: failed to unstage '{file}' in '{repositoryPath}'. {ex.Message}";
            }
        }
    }
}
#endif
