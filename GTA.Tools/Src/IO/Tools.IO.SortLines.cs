using System;
using System.Text.Json.Nodes;

namespace GTA.Tools
{
    internal static partial class ToolsIO
    {
        [ToolTag("io-specialist")]
        [FileMutating]
        [ToolInfo(
            name: "sort_lines",
            description: "Sort all lines in a file in-place, alphabetically. " +
                         "Supports reverse (descending) order and optional case-sensitive sorting. " +
                         "Useful for sorting lists, import statements, enum members, config keys, etc.")]
        [Parameter(index: 0, name: "path",
            type: "string",
            description: "Full absolute Windows path of the file to sort.",
            required: true)]
        [Parameter(index: 1, name: "descending",
            type: "boolean",
            description: "If true, sort in descending (Z–A) order. Defaults to false (ascending A–Z).",
            required: false)]
        [Parameter(index: 2, name: "match_case",
            type: "boolean",
            description: "If true, the sort is case-sensitive. Defaults to false (case-insensitive).",
            required: false)]
        public static object SortLines(JsonObject input)
        {
            string? err = ToolsIOHelpers.ResolveExistingFile(input, "path", out string path);
            if (err != null) return err;

            err = ToolsIOHelpers.ReadLines(path, out System.Collections.Generic.List<string> lines, out bool hadTrailingNewline);
            if (err != null) return err;

            if (lines.Count == 0)
                return "Error: File is empty — nothing to sort.";

            bool descending = ToolsIOHelpers.GetOptionalBool(input, "descending", false);
            bool matchCase  = ToolsIOHelpers.GetOptionalBool(input, "match_case", false);

            StringComparer comparer = matchCase
                ? StringComparer.Ordinal
                : StringComparer.OrdinalIgnoreCase;

            lines.Sort(comparer);
            if (descending) lines.Reverse();

            err = ToolsIOHelpers.WriteLines(path, lines, hadTrailingNewline);
            if (err != null) return err;

            string order = descending ? "descending (Z–A)" : "ascending (A–Z)";
            string cs    = matchCase  ? "case-sensitive"   : "case-insensitive";
            return $"Sorted {lines.Count} line(s) {order}, {cs}, in '{path}'.";
        }
    }
}
