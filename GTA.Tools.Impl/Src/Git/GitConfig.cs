#if !CO_WORKER_USE
using System;

namespace GTA.Tools.Impl.Git
{
    public static partial class Git
    {
        /// <summary>Sets a git configuration value.</summary>
        public static string Config(string repositoryPath, string key, string value, bool global = false)
        {
            try
            {
                return global
                    ? RunGitCommand(repositoryPath, "config", "--global", key, value)
                    : RunGitCommand(repositoryPath, "config", key, value);
            }
            catch (Exception ex)
            {
                return $"Error: failed to set git config '{key}' in '{repositoryPath}'. {ex.Message}";
            }
        }
    }
}
#endif
