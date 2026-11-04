using System;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace GTA.Tools
{
    internal static partial class ToolsIO
    {
        [ToolTag("directory")]
        [ToolInfo(
            name: "get_file_info",
            description: "Return detailed metadata about a file: size, creation time, last-modified time, " +
                         "last-access time, file extension, and whether it is read-only.")]
        [Parameter(index: 0, name: "path",
            type: "string",
            description: "Full absolute Windows path of the file.",
            required: true)]
        public static object GetFileInfo(JsonObject input)
        {
            string? err = ToolsIOHelpers.ResolveExistingFile(input, "path", out string path);
            if (err != null) return err;

            try
            {
                var fi = new FileInfo(path);

                var sb = new StringBuilder();
                sb.AppendLine($"File: {fi.FullName}");
                sb.AppendLine($"  Size          : {fi.Length} bytes ({ToolsIOHelpers.FormatBytes(fi.Length)})");
                sb.AppendLine($"  Extension     : {(string.IsNullOrEmpty(fi.Extension) ? "(none)" : fi.Extension)}");
                sb.AppendLine($"  Read-Only     : {fi.IsReadOnly}");
                sb.AppendLine($"  Created       : {fi.CreationTime:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"  Last Modified : {fi.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
                sb.Append    ($"  Last Accessed : {fi.LastAccessTime:yyyy-MM-dd HH:mm:ss}");

                return sb.ToString();
            }
            catch (UnauthorizedAccessException) { return $"Error: Access denied — cannot stat '{path}'."; }
            catch (IOException ex)              { return $"Error: IO error while reading info for '{path}': {ex.Message}"; }
            catch (Exception ex)                { return $"Error: Unexpected error while reading info for '{path}': {ex.Message}"; }
        }
    }
}
