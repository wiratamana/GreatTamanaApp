using System;
using System.IO;
using System.Text.Json.Nodes;

namespace GTA.Tools
{
    internal static partial class ToolsIO
    {
        [ToolTag("directory")]
        [FileMutating]
        [ToolInfo(
            name: "delete_file",
            description: "Delete a single file at the given path. " +
                         "Returns an error if the path does not exist or refers to a directory.")]
        [Parameter(index: 0, name: "path",
            type: "string",
            description: "Full absolute Windows path of the file to delete.",
            required: true)]
        public static object DeleteFile(JsonObject input)
        {
            string? err = ToolsIOHelpers.ResolveExistingFile(input, "path", out string path);
            if (err != null) return err;

            try
            {
                long size = new FileInfo(path).Length;
                File.Delete(path);
                return $"Deleted '{path}' ({ToolsIOHelpers.FormatBytes(size)}).";
            }
            catch (UnauthorizedAccessException) { return $"Error: Access denied — cannot delete '{path}'."; }
            catch (IOException ex)              { return $"Error: IO error while deleting '{path}': {ex.Message}"; }
            catch (Exception ex)                { return $"Error: Unexpected error while deleting '{path}': {ex.Message}"; }
        }
    }
}
