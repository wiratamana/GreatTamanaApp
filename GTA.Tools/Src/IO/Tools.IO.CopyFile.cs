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
            name: "copy_file",
            description: "Copy a file from source_path to dest_path. " +
                         "Creates any missing parent directories at the destination. " +
                         "By default overwrites the destination if it already exists.")]
        [Parameter(index: 0, name: "source_path",
            type: "string",
            description: "Full absolute Windows path of the file to copy.",
            required: true)]
        [Parameter(index: 1, name: "dest_path",
            type: "string",
            description: "Full absolute Windows path of the copy destination.",
            required: true)]
        [Parameter(index: 2, name: "overwrite",
            type: "boolean",
            description: "If true (default), overwrite the destination file if it already exists. " +
                         "If false, return an error instead of overwriting.",
            required: false)]
        public static object CopyFile(JsonObject input)
        {
            string? err = ToolsIOHelpers.ResolveExistingFile(input, "source_path", out string src);
            if (err != null) return err;

            err = ToolsIOHelpers.ResolveNewPath(input, "dest_path", out string dest);
            if (err != null) return err;

            if (Directory.Exists(dest))
                return $"Error: Destination '{dest}' is an existing directory. Provide a full file path.";

            bool overwrite = ToolsIOHelpers.GetOptionalBool(input, "overwrite", true);

            if (!overwrite && File.Exists(dest))
                return $"Error: Destination '{dest}' already exists and overwrite=false.";

            try
            {
                string? dir = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                File.Copy(src, dest, overwrite);

                long size = new FileInfo(dest).Length;
                return $"Copied '{src}' → '{dest}' ({ToolsIOHelpers.FormatBytes(size)}).";
            }
            catch (UnauthorizedAccessException) { return $"Error: Access denied while copying '{src}' to '{dest}'."; }
            catch (IOException ex)              { return $"Error: IO error while copying: {ex.Message}"; }
            catch (Exception ex)                { return $"Error: Unexpected error while copying: {ex.Message}"; }
        }
    }
}
