using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace GTA.Tools
{
    internal static partial class ToolsIO
    {
        [ToolTag("directory")]
        [ToolInfo(
            name: "find_files",
            description: "Find files matching a glob pattern and return a flat sorted list of absolute paths. " +
                         "Like 'dir /s /b *.cs' or 'find . -name \"*.log\"'. " +
                         "Unlike browse_dir_recursive (visual tree) this returns clean, " +
                         "one-per-line paths that are easy to feed into subsequent tool calls.")]
        [Parameter(index: 0, name: "path",
            type: "string",
            description: "Full absolute Windows path of the root directory to search from.",
            required: true)]
        [Parameter(index: 1, name: "pattern",
            type: "string",
            description: "Glob pattern to match file names, e.g. '*.log', '*.cs', 'Test*.txt'.",
            required: true)]
        [Parameter(index: 2, name: "recursive",
            type: "boolean",
            description: "If true (default), search all sub-directories as well.",
            required: false)]
        [Parameter(index: 3, name: "max_depth",
            type: "integer",
            description: "Maximum folder depth to traverse. 0 (default) means unlimited. " +
                         "Only meaningful when recursive=true.",
            required: false)]
        public static object FindFiles(JsonObject input)
        {
            string? err = ToolsIOHelpers.ResolveExistingDir(input, "path", out string root);
            if (err != null) return err;

            string? pattern = input["pattern"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(pattern))
                return "Error: 'pattern' parameter is missing or empty.";

            bool recursive = ToolsIOHelpers.GetOptionalBool(input, "recursive", true);

            int maxDepth = 0;
            JsonNode? depthNode = input["max_depth"];
            if (depthNode != null)
            {
                if (!ToolsIOHelpers.TryGetInt(depthNode, out maxDepth))
                    return "Error: 'max_depth' must be a valid integer.";

                if (maxDepth < 0)
                    return $"Error: 'max_depth' must be >= 0, but got {maxDepth}.";
            }

            try
            {
                var results     = new List<string>();
                var skippedDirs = new List<string>();

                if (!recursive || maxDepth == 1)
                {
                    string[] top = Directory.GetFiles(root, pattern, SearchOption.TopDirectoryOnly);
                    Array.Sort(top, StringComparer.OrdinalIgnoreCase);
                    results.AddRange(top);
                }
                else if (maxDepth == 0)
                {
                    string[] all = Directory.GetFiles(root, pattern, SearchOption.AllDirectories);
                    Array.Sort(all, StringComparer.OrdinalIgnoreCase);
                    results.AddRange(all);
                }
                else
                {
                    // Depth-limited recursive search
                    CollectFilesWithDepth(root, pattern, maxDepth, 1, results, skippedDirs);
                    results.Sort(StringComparer.OrdinalIgnoreCase);
                }

                if (results.Count == 0 && skippedDirs.Count == 0)
                    return $"No files matching '{pattern}' found under '{root}'" +
                           (recursive ? " (searched recursively)." : " (top-level only).");

                var sb = new StringBuilder();
                if (results.Count > 0)
                {
                    sb.AppendLine($"Found {results.Count} file(s) matching '{pattern}' under '{root}':");
                    sb.AppendLine();
                    foreach (string f in results)
                        sb.AppendLine(f);
                }
                else
                {
                    sb.AppendLine($"No files matching '{pattern}' found under '{root}'.");
                }

                if (skippedDirs.Count > 0)
                    sb.AppendLine($"\nWARNING: {skippedDirs.Count} directory/directories " +
                                  "could not be accessed and were skipped.");

                return sb.ToString();
            }
            catch (UnauthorizedAccessException) { return $"Error: Access denied while searching '{root}'."; }
            catch (IOException ex)              { return $"Error: IO error while searching '{root}': {ex.Message}"; }
            catch (Exception ex)                { return $"Error: Unexpected error while searching '{root}': {ex.Message}"; }
        }

        private static void CollectFilesWithDepth(
            string dir, string pattern, int maxDepth, int depth,
            List<string> results, List<string> skipped)
        {
            string[] dirFiles;
            try
            {
                dirFiles = Directory.GetFiles(dir, pattern, SearchOption.TopDirectoryOnly);
            }
            catch (UnauthorizedAccessException)
            {
                skipped.Add(dir);
                dirFiles = Array.Empty<string>();
            }
            foreach (string f in dirFiles)
                results.Add(f);

            if (depth >= maxDepth) return;

            string[] subDirs;
            try   { subDirs = Directory.GetDirectories(dir); }
            catch (UnauthorizedAccessException) { skipped.Add(dir); return; }

            foreach (string sub in subDirs)
            {
                try { CollectFilesWithDepth(sub, pattern, maxDepth, depth + 1, results, skipped); }
                catch (UnauthorizedAccessException) { skipped.Add(sub); }
            }
        }
    }
}
