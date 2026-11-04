using System;
using System.IO;
using System.Text.Json.Nodes;

namespace GTA.Tools
{
    internal static partial class ToolsIO
    {
        [ToolTag("directory")]
        [ToolInfo(
            name: "get_dir_size",
            description: "Calculate the total size of all files inside a directory. " +
                         "Reports total bytes (human-readable), file count, and folder count. " +
                         "Complements get_file_size which only works on single files.")]
        [Parameter(index: 0, name: "path",
            type: "string",
            description: "Full absolute Windows path of the directory.",
            required: true)]
        [Parameter(index: 1, name: "recursive",
            type: "boolean",
            description: "If true (default), include all sub-directories recursively.",
            required: false)]
        public static object GetDirSize(JsonObject input)
        {
            string? err = ToolsIOHelpers.ResolveExistingDir(input, "path", out string path);
            if (err != null) return err;

            bool recursive = ToolsIOHelpers.GetOptionalBool(input, "recursive", true);

            SearchOption option = recursive
                ? SearchOption.AllDirectories
                : SearchOption.TopDirectoryOnly;

            try
            {
                long totalBytes   = 0;
                int  fileCount    = 0;
                int  dirCount     = 0;
                int  skippedFiles = 0;

                foreach (string file in Directory.EnumerateFiles(path, "*", option))
                {
                    try
                    {
                        totalBytes += new FileInfo(file).Length;
                        fileCount++;
                    }
                    catch
                    {
                        skippedFiles++;
                    }
                }

                if (recursive)
                {
                    try
                    {
                        dirCount = Directory.GetDirectories(path, "*",
                                       SearchOption.AllDirectories).Length;
                    }
                    catch (UnauthorizedAccessException) { }
                }
                else
                {
                    try
                    {
                        dirCount = Directory.GetDirectories(path).Length;
                    }
                    catch (UnauthorizedAccessException) { }
                }

                string scopeNote  = recursive ? " (recursive)"      : " (top-level only)";
                string folderNote = recursive ? " (all, recursive)" : " (top-level only)";

                string result = $"Directory: '{path}'\n" +
                                $"  Files   : {fileCount:N0}{scopeNote}\n" +
                                $"  Folders : {dirCount:N0}{folderNote}\n" +
                                $"  Total   : {totalBytes:N0} bytes ({ToolsIOHelpers.FormatBytes(totalBytes)})";

                if (skippedFiles > 0)
                    result += $"\n  WARNING: {skippedFiles} file(s) could not be read (access denied or in use).";

                return result;
            }
            catch (UnauthorizedAccessException) { return $"Error: Access denied while enumerating '{path}'."; }
            catch (IOException ex)              { return $"Error: IO error while enumerating '{path}': {ex.Message}"; }
            catch (Exception ex)                { return $"Error: Unexpected error while enumerating '{path}': {ex.Message}"; }
        }
    }
}
