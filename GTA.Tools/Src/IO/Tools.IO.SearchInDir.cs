using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GTA.Tools
{
    internal static partial class ToolsIO
    {
        [ToolTag("directory")]
        [ToolInfo(
            name: "search_in_dir",
            description: "Search for a text pattern across all files (or files matching a glob filter) " +
                         "inside a directory. Returns a clean, compact listing grouped by file: " +
                         "the file's relative path on its own line, followed by '[line_number] line text' " +
                         "for each match. Long lines are truncated and results are capped " +
                         "(per-file and total) to keep the response small and easy to read for an LLM. " +
                         "Line numbers are 0-based (the first line of each file is index 0).")]
        [Parameter(index: 0, name: "path",
            type: "string",
            description: "Full absolute Windows path of the directory to search.",
            required: true)]
        [Parameter(index: 1, name: "content",
            type: "string",
            description: "The text or pattern to search for.",
            required: true)]
        [Parameter(index: 2, name: "filter",
            type: "string",
            description: "Glob file filter, e.g. '*.cs' or '*.txt'. Defaults to '*' (all files).",
            required: false)]
        [Parameter(index: 3, name: "recursive",
            type: "boolean",
            description: "If true (default), search all sub-directories as well.",
            required: false)]
        [Parameter(index: 4, name: "use_regex",
            type: "boolean",
            description: "If true, 'content' is treated as a .NET regular expression. Defaults to false.",
            required: false)]
        [Parameter(index: 5, name: "match_case",
            type: "boolean",
            description: "If true, matching is case-sensitive. Defaults to false (case-insensitive).",
            required: false)]
        [Parameter(index: 6, name: "max_matches_per_file",
            type: "integer",
            description: "Maximum number of matching lines to show per file. Extra matches are summarized " +
                         "as '... N more match(es) in this file'. Defaults to 20.",
            required: false)]
        [Parameter(index: 7, name: "max_total_matches",
            type: "integer",
            description: "Maximum total matching lines to show across all files before stopping the search " +
                         "early. Defaults to 200.",
            required: false)]
        [Parameter(index: 8, name: "max_line_length",
            type: "integer",
            description: "Matching lines longer than this are truncated with '…'. Defaults to 300 characters.",
            required: false)]
        public static object SearchInDir(JsonObject input)
        {
            string? err = ToolsIOHelpers.ResolveExistingDir(input, "path", out string dirPath);
            if (err != null) return err;

            string? query = input["content"]?.GetValue<string>();
            if (string.IsNullOrEmpty(query))
                return "Error: 'content' parameter is missing or empty.";

            string filter    = input["filter"]?.GetValue<string>()     ?? "*";
            bool   recursive = ToolsIOHelpers.GetOptionalBool(input, "recursive", true);
            bool   useRegex  = ToolsIOHelpers.GetOptionalBool(input, "use_regex", false);
            bool   matchCase = ToolsIOHelpers.GetOptionalBool(input, "match_case", false);

            int maxPerFile = ToolsIOHelpers.TryGetInt(input["max_matches_per_file"], out int mpf) ? mpf : 20;
            if (maxPerFile <= 0) maxPerFile = 20;

            int maxTotal = ToolsIOHelpers.TryGetInt(input["max_total_matches"], out int mt) ? mt : 200;
            if (maxTotal <= 0) maxTotal = 200;

            int maxLineLen = ToolsIOHelpers.TryGetInt(input["max_line_length"], out int mll) ? mll : 300;
            if (maxLineLen <= 0) maxLineLen = 300;

            string pattern = useRegex ? query : Regex.Escape(query);
            RegexOptions options = matchCase ? RegexOptions.None : RegexOptions.IgnoreCase;

            Regex regex;
            try
            {
                regex = new Regex(pattern, options, TimeSpan.FromSeconds(5));
            }
            catch (ArgumentException ex)
            {
                return $"Error: Invalid regex pattern — {ex.Message}";
            }

            SearchOption searchOption = recursive
                ? SearchOption.AllDirectories
                : SearchOption.TopDirectoryOnly;

            string[] files;
            try
            {
                files = Directory.GetFiles(dirPath, filter, searchOption);
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            }
            catch (UnauthorizedAccessException) { return $"Error: Access denied while enumerating '{dirPath}'."; }
            catch (IOException ex)              { return $"Error: IO error while enumerating '{dirPath}': {ex.Message}"; }
            catch (Exception ex)                { return $"Error: Unexpected error while enumerating '{dirPath}': {ex.Message}"; }

            if (files.Length == 0)
                return $"No files found in '{dirPath}' matching filter '{filter}'.";

            var sb         = new StringBuilder();
            int totalFiles = 0;
            int totalHits  = 0;
            bool truncatedGlobally = false;

            const long MAX_SEARCH_FILE_BYTES = 50L * 1024 * 1024; // 50 MB

            foreach (string file in files)
            {
                if (totalHits >= maxTotal)
                {
                    truncatedGlobally = true;
                    break;
                }

                // Cheap check first: known binary extensions never get opened as text.
                if (ToolsIOHelpers.IsBinaryExtension(file))
                    continue;

                long fileLen;
                try { fileLen = new FileInfo(file).Length; }
                catch { continue; }

                if (fileLen == 0 || fileLen > MAX_SEARCH_FILE_BYTES)
                    continue;

                // Fallback for binary files with no recognizable extension: sniff for NUL bytes.
                if (ToolsIOHelpers.IsBinaryContent(file))
                    continue;

                string[] lines;
                try
                {
                    lines = File.ReadAllLines(file, Encoding.UTF8);
                }
                catch
                {
                    continue;
                }

                var fileHits = new List<(int idx, string text)>();
                int fileMatchCount = 0;

                for (int i = 0; i < lines.Length; i++)
                {
                    bool match;
                    try   { match = regex.IsMatch(lines[i]); }
                    catch (RegexMatchTimeoutException) { continue; }

                    if (!match) continue;

                    fileMatchCount++;

                    if (fileHits.Count < maxPerFile && totalHits + fileHits.Count < maxTotal)
                        fileHits.Add((i, lines[i]));
                }

                if (fileMatchCount == 0) continue;

                totalFiles++;
                totalHits += fileHits.Count;

                string rel = Path.GetRelativePath(dirPath, file);

                if (sb.Length > 0) sb.Append('\n');
                sb.Append(rel).Append('\n');

                foreach (var (idx, text) in fileHits)
                {
                    string line = text.Trim();
                    if (line.Length > maxLineLen)
                        line = line.Substring(0, maxLineLen) + "…";

                    sb.Append('[').Append(idx).Append("] ").Append(line).Append('\n');
                }

                int remaining = fileMatchCount - fileHits.Count;
                if (remaining > 0)
                    sb.Append("... ").Append(remaining).Append(" more match(es) in this file\n");
            }

            if (totalHits == 0)
                return $"No matches found for \"{query}\" in '{dirPath}' (filter: {filter}).";

            string header = $"Found {totalHits} match(es) in {totalFiles} file(s) for \"{query}\" (filter: {filter})" +
                             (truncatedGlobally ? $" — stopped early at max_total_matches={maxTotal}" : "") + ":\n\n";

            return header + sb.ToString();
        }
    }
}
