#if !CO_WORKER_USE
using System;

namespace GTA.Tools.Impl.Git
{
    public static partial class Git
    {
        /// <summary>Marks a specific point in history with a tag.</summary>
        public static string Tag(string repositoryPath, string tagName)
        {
            try
            {
                return RunGitCommand(repositoryPath, "tag", tagName);
            }
            catch (Exception ex)
            {
                return $"Error: failed to create tag '{tagName}' in '{repositoryPath}'. {ex.Message}";
            }
        }
    }
}
#endif
