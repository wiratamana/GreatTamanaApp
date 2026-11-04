using System;
using System.Text;
using System.Text.Json.Nodes;

namespace GTA.Tools
{
    internal static partial class ToolsIO
    {
        [ToolTag("io-specialist")]
        [FileMutating]
        [ToolInfo(
            name: "remove_line",
            description: "Remove one or more consecutive lines starting at the given 0-based index. " +
                         "Similar to List.RemoveRange(index, count). " +
                         "If the range extends beyond the last line it is silently clamped. " +
                         "All lines below the removed range shift up. " +
                         "The result shows a few context lines around the removal point (default 2, see " +
                         "'context_lines'), all numbered with their 0-based index in [NNN] format, and tells " +
                         "you how far the lines below moved. " +
                         "THE RESULT IS THE FINAL SAVED CONTENT around the removal — do NOT call read_line/read_file " +
                         "afterwards just to verify (a duplicate read of the same lines in the same round is " +
                         "skipped automatically). " +
                         "Line numbers are 0-based (the first line of the file is index 0).")]
        [Parameter(index: 0, name: "path",
            type: "string",
            description: "Full absolute Windows path of the file to modify.",
            required: true)]
        [Parameter(index: 1, name: "line_index",
            type: "integer",
            description: "0-based index of the (first) line to remove. Valid range: 0 to (line count - 1).",
            required: true)]
        [Parameter(index: 2, name: "length",
            type: "integer",
            description: "Number of lines to remove. Must be >= 1. Defaults to 1 (removes a single line).",
            required: false)]
        [Parameter(index: 3, name: "context_lines",
            type: "integer",
            description: "How many unchanged lines to show before and after the removal point in the result. " +
                         "Default 2, range 0-20.",
            required: false)]
        public static object RemoveLine(JsonObject input)
        {
            string? err = ToolsIOHelpers.ResolvePath(input, out string path);
            if (err != null) return err;

            err = ToolsIOHelpers.ReadLines(path, out System.Collections.Generic.List<string> lines, out bool hadTrailingNewline);
            if (err != null) return err;

            if (lines.Count == 0)
                return $"Error: File is empty — nothing to remove.";

            err = ResolveLineIndex(input, lines.Count, out int index);
            if (err != null) return err;

            int length = 1;
            JsonNode? lenNode = input["length"];
            if (lenNode != null)
            {
                if (!ToolsIOHelpers.TryGetInt(lenNode, out length))
                    return "Error: 'length' must be a valid integer.";

                if (length < 1)
                    return $"Error: 'length' must be >= 1, but got {length}.";
            }

            int actualLength = Math.Min(length, lines.Count - index);
            string label = actualLength == 1
                ? $"[{index}]: \"{lines[index]}\""
                : $"[{index}–{index + actualLength - 1}]";

            int oldLineCount = lines.Count;
            lines.RemoveRange(index, actualLength);

            err = ToolsIOHelpers.WriteLines(path, lines, hadTrailingNewline);
            if (err != null) return err;

            string summary = actualLength == 1
                ? $"Removed line {label}. File now has {lines.Count} line(s)."
                : $"Removed {actualLength} line(s) {label}. File now has {lines.Count} line(s).";

            // Shared builder (Tools.IO.cs): context around the removal point + shift
            // note + "saved" note, and records what was shown in the FileViewLedger.
            return BuildMutationResult(
                "remove_line", path, summary, null, lines,
                regionStart:  index,
                regionEnd:    index,
                contextLines: ResolveContextLines(input),
                oldLineCount: oldLineCount);
        }
    }
}
