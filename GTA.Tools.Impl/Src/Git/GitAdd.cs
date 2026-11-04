#if !CO_WORKER_USE
using System;

namespace GTA.Tools.Impl.Git
{
    public static partial class Git
    {
        public static string Add(string repositoryPath, string pathspec = ".")
        {
            try
            {
                return RunGitCommand(repositoryPath, "add", pathspec);
            }
            catch (Exception ex)
            {
                return $"Error: failed to stage '{pathspec}' in '{repositoryPath}'. {ex.Message}";
            }
        }
    }
}
#endif
