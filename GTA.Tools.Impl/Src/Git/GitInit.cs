#if !CO_WORKER_USE
using System;
using System.IO;

namespace GTA.Tools.Impl.Git
{
    public static partial class Git
    {
        /// <summary>Creates a new, empty Git repository at <paramref name="repositoryPath"/> (git init).</summary>
        public static string Init(string repositoryPath)
        {
            try
            {
                if (!Directory.Exists(repositoryPath))
                    Directory.CreateDirectory(repositoryPath);

                return RunGitCommand(repositoryPath, "init");
            }
            catch (Exception ex)
            {
                return $"Error: failed to initialize git repository at '{repositoryPath}'. {ex.Message}";
            }
        }
    }
}
#endif
