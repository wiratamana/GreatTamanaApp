#if !CO_WORKER_USE
using System;

namespace GTA.Tools.Impl.Git
{
    public static partial class Git
    {
        public static string Revert(string repositoryPath, string commitHash)
        {
            try
            {
                return RunGitCommand(repositoryPath, "revert", "--no-edit", commitHash);
            }
            catch (Exception ex)
            {
                return $"Error: failed to revert commit '{commitHash}' in '{repositoryPath}'. {ex.Message}";
            }
        }
    }
}
#endif
