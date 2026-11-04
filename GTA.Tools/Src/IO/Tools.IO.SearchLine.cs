using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GTA.Tools
{
    internal static partial class ToolsIO
    {
        [ToolTag("essential")]
        [ToolInfo(
            name: "search_line",
            description: "Search for text in a file and return every matching line with its 0-based index. " +
                         "Similar to the VS Code search function. " +
                         "Returns all matches or 'No matches found' when nothing is found. " +
                         "Line numbers are 0-based (the first line of the file is index 0).")]
        [Parameter(index: 0, name: "path",
            type: "string",
            description: "Full absolute Windows path of the file to search.",
            required: true)]
        [Parameter(index: 1, name: "content",
            type: "string",
            description: "The text or pattern to search for.",
            required: true)]
        [Parameter(index: 2, name: "match_case",
            type: "boolean",
            description: "If true, matching is case-sensitive. Defaults to false (case-insensitive).",
            required: false)]
        [Parameter(index: 3, name: "match_whole_word",
            type: "boolean",
            description: "If true, only match the whole word (surrounded by word boundaries). Defaults to false.",
            required: false)]
        [Parameter(index: 4, name: "use_regex",
            type: "boolean",
            description: "If true, 'content' is treated as a regular expression pattern. Defaults to false.",
            required: false)]
        public static object SearchLine(JsonObject input)
        {
            string? err = ToolsIOHelpers.ResolvePath(input, out string path);
            if (err != null) return err;

            string? query = input["content"]?.GetValue<string>();
            if (string.IsNullOrEmpty(query))
                return "Error: 'content' parameter is missing or empty.";

            bool matchCase      = ToolsIOHelpers.GetOptionalBool(input, "match_case", false);
            bool matchWholeWord = ToolsIOHelpers.GetOptionalBool(input, "match_whole_word", false);
            bool useRegex       = ToolsIOHelpers.GetOptionalBool(input, "use_regex", false);

            string pattern = useRegex ? query : Regex.Escape(query);
            if (matchWholeWord) pattern = $@"\b{pattern}\b";

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

            err = ToolsIOHelpers.ReadLines(path, out List<string> lines, out _);
            if (err != null) return err;

            var matches = new List<(int index, string text)>();
            for (int i = 0; i < lines.Count; i++)
            {
                try
                {
                    if (regex.IsMatch(lines[i]))
                        matches.Add((i, lines[i]));
                }
                catch (RegexMatchTimeoutException)
                {
                    return $"Error: Regex match timed out on line {i}. " +
                           $"The pattern may be too complex for this file.";
                }
            }

            if (matches.Count == 0)
                return $"No matches found for \"{query}\".";

            int idxPad = Math.Max(lines.Count >= 1000 ? 4 : 3, (lines.Count - 1).ToString().Length);

            var sb = new StringBuilder();
            sb.AppendLine($"Found {matches.Count} match(es) for \"{query}\":");
            foreach (var (idx, text) in matches)
            {
                sb.Append("  [");
                sb.Append(idx.ToString().PadLeft(idxPad, '0'));
                sb.Append("] ");
                sb.AppendLine(text);
            }

            return sb.ToString();
        }
    }
}
