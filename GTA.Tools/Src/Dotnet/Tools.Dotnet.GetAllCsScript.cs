#if !CO_WORKER_USE
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace GTA.Tools
{
    internal static partial class ToolsDotnet
    {
        [ToolTag("external")]
        [ToolInfo(
            name: "dotnet_get_all_cs_files",
            description: "Recursively list every *.cs source file under a project/solution directory and return " +
                         "the result as a plain, newline-separated list of absolute file paths.")]
        [Parameter(index: 0, name: "path",
            type: "string",
            description: "Full absolute Windows path of the root directory to search from (project or solution folder).",
            required: true)]
        [Parameter(index: 1, name: "exclude_bin_obj",
            type: "boolean",
            description: "If true (default), skip files located under any 'bin' or 'obj' folder (build output/intermediates).",
            required: false)]
        public static object DotnetGetAllCsScript(JsonObject input)
        {
            string? err = ToolsIOHelpers.ResolveExistingDir(input, "path", out string root);
            if (err != null) return err;

            bool excludeBinObj = ToolsIOHelpers.GetOptionalBool(input, "exclude_bin_obj", true);

            try
            {
                string[] all = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories);
                Array.Sort(all, StringComparer.OrdinalIgnoreCase);

                var results = new List<string>();
                foreach (string f in all)
                {
                    if (excludeBinObj && IsUnderBinOrObj(root, f))
                        continue;

                    results.Add(f);
                }

                if (results.Count == 0)
                    return $"No *.cs files found under '{root}'.";

                var sb = new StringBuilder();
                sb.AppendLine($"Found {results.Count} *.cs file(s) under '{root}':");
                sb.AppendLine();
                foreach (string f in results)
                    sb.AppendLine(f);

                return sb.ToString().TrimEnd();
            }
            catch (UnauthorizedAccessException) { return $"Error: Access denied while searching '{root}'."; }
            catch (IOException ex)              { return $"Error: IO error while searching '{root}': {ex.Message}"; }
            catch (Exception ex)                { return $"Error: Unexpected error while searching '{root}': {ex.Message}"; }
        }

        /// <summary>True if <paramref name="file"/> has a 'bin' or 'obj' path segment relative to <paramref name="root"/>.</summary>
        private static bool IsUnderBinOrObj(string root, string file)
        {
            string rel = Path.GetRelativePath(root, file);
            string[] parts = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            foreach (string p in parts)
            {
                if (p.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                    p.Equals("obj", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}
#endif
