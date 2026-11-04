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
            name: "create_dir",
            description: "Create a directory and all required parent directories. " +
                         "Succeeds silently if the directory already exists.")]
        [Parameter(index: 0, name: "path",
            type: "string",
            description: "Full absolute Windows path of the directory to create.",
            required: true)]
        public static object CreateDir(JsonObject input)
        {
            string? raw = input["path"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(raw))
                return "Error: 'path' parameter is missing or empty.";

            string path = ToolsIOHelpers.CleanPath(raw);

            if (File.Exists(path))
                return $"Error: '{path}' already exists as a file, not a directory.";

            try
            {
                bool existed = Directory.Exists(path);
                Directory.CreateDirectory(path);
                return existed
                    ? $"Directory already exists: '{path}'."
                    : $"Directory created: '{path}'.";
            }
            catch (UnauthorizedAccessException) { return $"Error: Access denied — cannot create '{path}'."; }
            catch (IOException ex)              { return $"Error: IO error while creating '{path}': {ex.Message}"; }
            catch (Exception ex)                { return $"Error: Unexpected error while creating '{path}': {ex.Message}"; }
        }
    }
}
