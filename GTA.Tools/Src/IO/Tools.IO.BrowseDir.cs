using System;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace GTA.Tools
{
    internal static partial class ToolsIO
    {
        [ToolTag("essential", "bootstrap")]
        [ToolInfo(
            name: "browse_dir",
            description: "List all files and folders inside a directory, similar to the Linux 'ls' command. " +
                         "Folders are shown as [folder] 'Name' and files as [file] 'Name'. " +
                         "Entries are sorted: all folders first (A–Z), then all files (A–Z). " +
                         "Pass details=true to also show file sizes and last-write timestamps.")]
        [Parameter(index: 0, name: "path",
            type: "string",
            description: "Full absolute Windows path of the directory to browse (e.g. C:\\project\\src).",
            required: true)]
        [Parameter(index: 1, name: "details",
            type: "boolean",
            description: "If true, each file entry also shows its size and last-write date. Defaults to false.",
            required: false)]
        public static object BrowseDir(JsonObject input)
        {
            string? err = ToolsIOHelpers.ResolveDir(input, out string path);
            if (err != null) return err;

            bool details = ToolsIOHelpers.GetOptionalBool(input, "details", false);

            try
            {
                string[] dirs  = Directory.GetDirectories(path);
                string[] files = Directory.GetFiles(path);

                Array.Sort(dirs,  StringComparer.OrdinalIgnoreCase);
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);

                if (dirs.Length == 0 && files.Length == 0)
                    return $"Directory is empty: {path}";

                var sb = new StringBuilder();
                sb.AppendLine($"Contents of: {path}");
                sb.AppendLine();

                foreach (string dir in dirs)
                    sb.AppendLine($"  [folder] '{Path.GetFileName(dir)}'");

                foreach (string file in files)
                {
                    if (details)
                    {
                        var fi      = new FileInfo(file);
                        string size = ToolsIOHelpers.FormatBytes(fi.Length);
                        string date = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm");
                        sb.AppendLine($"  [file]   '{Path.GetFileName(file)}'   {size}   {date}");
                    }
                    else
                    {
                        sb.AppendLine($"  [file]   '{Path.GetFileName(file)}'");
                    }
                }

                sb.AppendLine();
                sb.Append($"  {dirs.Length} folder(s), {files.Length} file(s)");

                return sb.ToString();
            }
            catch (UnauthorizedAccessException)  { return $"Error: Access denied — cannot list '{path}'."; }
            catch (IOException ex)               { return $"Error: IO error while listing '{path}': {ex.Message}"; }
            catch (Exception ex)                 { return $"Error: Unexpected error while listing '{path}': {ex.Message}"; }
        }
    }
}
