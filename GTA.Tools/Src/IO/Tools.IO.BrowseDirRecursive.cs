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
            name: "browse_dir_recursive",
            description: "Recursively list all files and sub-directories under a root directory, " +
                         "formatted as an indented tree (like the Windows 'tree' command). " +
                         "Supports an optional depth limit and file-type filter.")]
        [Parameter(index: 0, name: "path",
            type: "string",
            description: "Full absolute Windows path of the root directory to list.",
            required: true)]
        [Parameter(index: 1, name: "max_depth",
            type: "integer",
            description: "Maximum folder depth to traverse. " +
                         "1 = root contents only, 2 = root + one sub-level, etc. " +
                         "0 (default) means unlimited depth (capped at 64 levels to prevent stack overflow).",
            required: false)]
        [Parameter(index: 2, name: "filter",
            type: "string",
            description: "Glob pattern to filter which files are shown, e.g. '*.cs'. " +
                         "Directories are always shown regardless of this filter. " +
                         "Defaults to '*' (show all files).",
            required: false)]
        public static object BrowseDirRecursive(JsonObject input)
        {
            string? err = ToolsIOHelpers.ResolveExistingDir(input, "path", out string root);
            if (err != null) return err;

            int maxDepth = 0;
            JsonNode? depthNode = input["max_depth"];
            if (depthNode != null)
            {
                if (!ToolsIOHelpers.TryGetInt(depthNode, out maxDepth))
                    return "Error: 'max_depth' must be a valid integer.";
                if (maxDepth < 0)
                    return $"Error: 'max_depth' must be >= 0, but got {maxDepth}.";
            }

            string filter = input["filter"]?.GetValue<string>() ?? "*";

            var sb = new StringBuilder();
            sb.AppendLine(root);

            int totalDirs = 0, totalFiles = 0;

            try
            {
                BuildTree(root, "", 1, maxDepth, filter, sb, ref totalDirs, ref totalFiles);
            }
            catch (UnauthorizedAccessException) { return $"Error: Access denied while traversing '{root}'."; }
            catch (IOException ex)              { return $"Error: IO error while traversing '{root}': {ex.Message}"; }
            catch (Exception ex)                { return $"Error: Unexpected error while traversing '{root}': {ex.Message}"; }

            sb.AppendLine();
            sb.Append($"{totalDirs} folder(s), {totalFiles} file(s)");
            if (!string.IsNullOrEmpty(filter) && filter != "*")
                sb.Append($" matching '{filter}'");

            return sb.ToString();
        }

        private const int ABSOLUTE_MAX_DEPTH = 64;

        private static void BuildTree(
            string        currentPath,
            string        indent,
            int           depth,
            int           maxDepth,
            string        filter,
            StringBuilder sb,
            ref int       dirCount,
            ref int       fileCount)
        {
            string[] subdirs;
            try   { subdirs = Directory.GetDirectories(currentPath); }
            catch (UnauthorizedAccessException)
            {
                sb.AppendLine($"{indent}[access denied]");
                return;
            }
            catch (IOException ex)
            {
                sb.AppendLine($"{indent}[IO error: {ex.Message}]");
                return;
            }

            string[] files;
            try   { files = Directory.GetFiles(currentPath, filter); }
            catch (UnauthorizedAccessException) { files = Array.Empty<string>(); }
            catch (IOException)                 { files = Array.Empty<string>(); }

            Array.Sort(subdirs, StringComparer.OrdinalIgnoreCase);
            Array.Sort(files,   StringComparer.OrdinalIgnoreCase);

            int totalEntries = subdirs.Length + files.Length;
            int entryIdx     = 0;

            foreach (string subdir in subdirs)
            {
                entryIdx++;
                bool isLast = (entryIdx == totalEntries);

                string connector   = isLast ? "└── " : "├── ";
                string childIndent = indent + (isLast ? "    " : "│   ");

                sb.AppendLine($"{indent}{connector}[{Path.GetFileName(subdir)}/]");
                dirCount++;

                if (depth >= ABSOLUTE_MAX_DEPTH)
                {
                    sb.AppendLine($"{childIndent}[depth limit reached — {ABSOLUTE_MAX_DEPTH} levels max]");
                }
                else if (maxDepth == 0 || depth < maxDepth)
                {
                    BuildTree(subdir, childIndent, depth + 1, maxDepth, filter, sb,
                              ref dirCount, ref fileCount);
                }
                else
                {
                    sb.AppendLine($"{childIndent}...");
                }
            }

            foreach (string file in files)
            {
                entryIdx++;
                bool isLast = (entryIdx == totalEntries);

                string connector = isLast ? "└── " : "├── ";
                sb.AppendLine($"{indent}{connector}{Path.GetFileName(file)}");
                fileCount++;
            }
        }
    }
}
