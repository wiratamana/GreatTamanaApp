using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace GTA.Tools
{
    internal static partial class ToolsIO
    {
        [ToolTag("io-specialist")]
        [FileMutating]
        [ToolInfo(
            name: "deduplicate_lines",
            description: "Remove duplicate lines from a file and write the result back. " +
                         "Two modes: (a) global — remove all duplicates keeping the first occurrence; " +
                         "(b) adjacent — remove only consecutive duplicates (like Unix 'uniq'). " +
                         "Useful for cleaning up log files, import lists, or any repetitive content.")]
        [Parameter(index: 0, name: "path",
            type: "string",
            description: "Full absolute Windows path of the file to deduplicate.",
            required: true)]
        [Parameter(index: 1, name: "adjacent",
            type: "boolean",
            description: "If true, only remove consecutive (adjacent) duplicate lines, like Unix 'uniq'. " +
                         "Default false (remove all duplicates globally, keeping first occurrence).",
            required: false)]
        [Parameter(index: 2, name: "match_case",
            type: "boolean",
            description: "If true, comparison is case-sensitive. Defaults to false (case-insensitive).",
            required: false)]
        public static object DeduplicateLines(JsonObject input)
        {
            string? err = ToolsIOHelpers.ResolveExistingFile(input, "path", out string path);
            if (err != null) return err;

            err = ToolsIOHelpers.ReadLines(path, out List<string> lines, out bool hadTrailingNewline);
            if (err != null) return err;

            if (lines.Count == 0)
                return "Error: File is empty — nothing to deduplicate.";

            bool adjacent  = ToolsIOHelpers.GetOptionalBool(input, "adjacent", false);
            bool matchCase = ToolsIOHelpers.GetOptionalBool(input, "match_case", false);

            StringComparer comparer = matchCase
                ? StringComparer.Ordinal
                : StringComparer.OrdinalIgnoreCase;

            int originalCount = lines.Count;
            var result        = new List<string>(lines.Count);

            if (adjacent)
            {
                result.Add(lines[0]);
                for (int i = 1; i < lines.Count; i++)
                    if (comparer.Compare(lines[i], lines[i - 1]) != 0)
                        result.Add(lines[i]);
            }
            else
            {
                var seen = new HashSet<string>(comparer);
                foreach (string line in lines)
                    if (seen.Add(line))
                        result.Add(line);
            }

            int removed = originalCount - result.Count;

            if (removed == 0)
                return $"No duplicate lines found. File unchanged ({lines.Count} line(s)).";

            err = ToolsIOHelpers.WriteLines(path, result, hadTrailingNewline);
            if (err != null) return err;

            string mode = adjacent ? "adjacent" : "global";
            return $"Removed {removed} duplicate line(s) ({mode} mode). " +
                   $"File now has {result.Count} line(s) (was {originalCount}).";
        }
    }
}
