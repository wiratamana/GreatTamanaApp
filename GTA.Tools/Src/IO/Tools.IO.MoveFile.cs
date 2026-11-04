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
            name: "move_file",
            description: "Move (or rename) a file from source_path to dest_path. " +
                         "Creates any missing parent directories at the destination. " +
                         "By default does NOT overwrite an existing destination.")]
        [Parameter(index: 0, name: "source_path",
            type: "string",
            description: "Full absolute Windows path of the file to move.",
            required: true)]
        [Parameter(index: 1, name: "dest_path",
            type: "string",
            description: "Full absolute Windows path of the move destination (new name/location).",
            required: true)]
        [Parameter(index: 2, name: "overwrite",
            type: "boolean",
            description: "If true, overwrite the destination file if it already exists. " +
                         "Defaults to false (safe: fails instead of silently overwriting).",
            required: false)]
        public static object MoveFile(JsonObject input)
        {
            string? err = ToolsIOHelpers.ResolveExistingFile(input, "source_path", out string src);
            if (err != null) return err;

            err = ToolsIOHelpers.ResolveNewPath(input, "dest_path", out string dest);
            if (err != null) return err;

            if (Directory.Exists(dest))
                return $"Error: Destination '{dest}' is an existing directory. Provide a full file path.";

            bool overwrite = ToolsIOHelpers.GetOptionalBool(input, "overwrite", false);

            if (!overwrite && File.Exists(dest))
                return $"Error: Destination '{dest}' already exists and overwrite=false.";

            try
            {
                string? dir = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                File.Move(src, dest, overwrite);
                return $"Moved '{src}' → '{dest}'.";
            }
            catch (UnauthorizedAccessException) { return $"Error: Access denied while moving '{src}' to '{dest}'."; }
            catch (IOException ex)              { return $"Error: IO error while moving: {ex.Message}"; }
            catch (Exception ex)                { return $"Error: Unexpected error while moving: {ex.Message}"; }
        }
    }
}
