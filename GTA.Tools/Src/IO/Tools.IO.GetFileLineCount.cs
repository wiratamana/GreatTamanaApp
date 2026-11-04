using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace GTA.Tools
{
    internal static partial class ToolsIO
    {
        [ToolTag("essential")]
        [ToolInfo(
            name: "get_file_line_count",
            description: "Return the total number of lines in a text file.")]
        [Parameter(index: 0, name: "path",
            type: "string",
            description: "Full absolute Windows path of the file.",
            required: true)]
        public static object GetFileLineCount(JsonObject input)
        {
            string? err = ToolsIOHelpers.ResolvePath(input, out string path);
            if (err != null) return err;

            try
            {
                int count = File.ReadLines(path, Encoding.UTF8).Count();
                return $"{count} lines";
            }
            catch (UnauthorizedAccessException)  { return $"Error: Access denied — cannot read '{path}'."; }
            catch (IOException ex)               { return $"Error: IO error while reading '{path}': {ex.Message}"; }
            catch (Exception ex)                 { return $"Error: Unexpected error while reading '{path}': {ex.Message}"; }
        }
    }
}
