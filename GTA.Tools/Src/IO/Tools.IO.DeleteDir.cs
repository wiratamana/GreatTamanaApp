using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace GTA.Tools
{
    internal static partial class ToolsIO
    {
        [ToolTag("directory")]
        [FileMutating]
        [ToolInfo(
            name: "delete_dir",
            description: "Delete a directory. " +
                         "When recursive=false (the safe default), fails if the directory is non-empty. " +
                         "When recursive=true, deletes the directory and ALL its contents. " +
                         "Pass dry_run=true to preview what would be deleted without making any changes.")]
        [Parameter(index: 0, name: "path",
            type: "string",
            description: "Full absolute Windows path of the directory to delete.",
            required: true)]
        [Parameter(index: 1, name: "recursive",
            type: "boolean",
            description: "If true, delete the directory and all its contents recursively. " +
                         "Defaults to false (only deletes empty directories).",
            required: false)]
        [Parameter(index: 2, name: "dry_run",
            type: "boolean",
            description: "If true, list what would be deleted without actually deleting anything. " +
                         "Useful to confirm the scope of a recursive delete before committing.",
            required: false)]
        public static object DeleteDir(JsonObject input)
        {
            string? err = ToolsIOHelpers.ResolveExistingDir(input, "path", out string path);
            if (err != null) return err;

            bool recursive = ToolsIOHelpers.GetOptionalBool(input, "recursive", false);
            bool dryRun    = ToolsIOHelpers.GetOptionalBool(input, "dry_run", false);

            if (dryRun)
            {
                try
                {
                    var entries = Directory.GetFileSystemEntries(path, "*", SearchOption.AllDirectories);
                    int  dirCount   = entries.Count(e => Directory.Exists(e));
                    int  fileCount  = 0;
                    long totalSize  = 0;
                    int  accessFail = 0;
                    foreach (string e in entries.Where(e => File.Exists(e)))
                    {
                        fileCount++;
                        try   { totalSize += new FileInfo(e).Length; }
                        catch { accessFail++; }
                    }

                    string dryRunMsg = $"DRY RUN — Would delete '{path}':\n" +
                                       $"  {fileCount} file(s), {dirCount} folder(s), {ToolsIOHelpers.FormatBytes(totalSize)} total.\n" +
                                       $"Pass dry_run=false (and recursive=true if needed) to confirm.";
                    if (accessFail > 0)
                        dryRunMsg += $"\n  NOTE: {accessFail} file(s) could not be stat'd (total size may be incomplete).";
                    return dryRunMsg;
                }
                catch (UnauthorizedAccessException) { return $"Error: Access denied while enumerating '{path}'."; }
                catch (IOException ex)              { return $"Error: IO error while enumerating '{path}': {ex.Message}"; }
                catch (Exception ex)                { return $"Error: Unexpected error while enumerating '{path}': {ex.Message}"; }
            }

            if (!recursive)
            {
                string[] children = Directory.GetFileSystemEntries(path);
                if (children.Length > 0)
                    return $"Error: Directory '{path}' is not empty ({children.Length} item(s)). " +
                           $"Pass recursive=true to delete it along with all its contents, " +
                           $"or dry_run=true to preview what would be removed.";
            }

            try
            {
                Directory.Delete(path, recursive);
                return recursive
                    ? $"Deleted directory and all contents: '{path}'."
                    : $"Deleted empty directory: '{path}'.";
            }
            catch (UnauthorizedAccessException) { return $"Error: Access denied — cannot delete '{path}'."; }
            catch (IOException ex)              { return $"Error: IO error while deleting '{path}': {ex.Message}"; }
            catch (Exception ex)                { return $"Error: Unexpected error while deleting '{path}': {ex.Message}"; }
        }
    }
}
