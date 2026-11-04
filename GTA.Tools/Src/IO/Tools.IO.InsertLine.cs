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
            name: "insert_line",
            description: "Insert one or more new lines at the given 0-based index, shifting all lines below them down. " +
                         "Similar to List.InsertRange(index, items). " +
                         "Use index 0 to insert before the first line. " +
                         "Use index equal to the current line count to append after the last line. " +
                         "The result shows the inserted lines with a few context lines before and after them " +
                         "(default 2, see 'context_lines'), all numbered with their 0-based index in [NNN] format, " +
                         "and tells you how far the lines below moved. " +
                         "THE RESULT IS THE FINAL SAVED CONTENT of that region — do NOT call read_line/read_file " +
                         "afterwards just to verify the insert (a duplicate read of the same lines in the same round " +
                         "is skipped automatically). Very long inserted blocks are echoed back only at their " +
                         "first/last lines. " +
                         "Line numbers are 0-based (the first line of the file is index 0).")]
        [Parameter(index: 0, name: "path",
            type: "string",
            description: "Full absolute Windows path of the file to modify.",
            required: true)]
        [Parameter(index: 1, name: "contents",
            type: "string",
            description: "The line(s) to insert, separated by '\\n'. Each segment becomes one new line in the file.",
            required: true)]
        [Parameter(index: 2, name: "line_index",
            type: "integer",
            description: "0-based index at which to insert the new line(s). " +
                         "Valid range: 0 to current line count (inclusive).",
            required: true)]
        [Parameter(index: 3, name: "preserve_trailing_newline",
            type: "boolean",
            description: "If true, a trailing blank line at the end of 'contents' is kept as an " +
                         "explicit blank line. By default (false) a trailing '\\n' in 'contents' does " +
                         "not insert an extra blank line.",
            required: false)]
        [Parameter(index: 4, name: "context_lines",
            type: "integer",
            description: "How many unchanged lines to show before and after the inserted region in the result. " +
                         "Default 2, range 0-20.",
            required: false)]
        public static object InsertLine(JsonObject input)
        {
            string? err = ToolsIOHelpers.ResolvePath(input, out string path);
            if (err != null) return err;

            err = ToolsIOHelpers.ReadLines(path, out System.Collections.Generic.List<string> lines, out bool hadTrailingNewline);
            if (err != null) return err;

            string? raw = input["contents"]?.GetValue<string>();
            if (raw == null)
                return "Error: 'contents' parameter is missing.";

            string[] newLines = raw.Split('\n');
            for (int i = 0; i < newLines.Length; i++)
                newLines[i] = newLines[i].TrimEnd('\r');

            bool preserveTrailing = ToolsIOHelpers.GetOptionalBool(input, "preserve_trailing_newline", false);
            if (!preserveTrailing && newLines.Length > 0 && newLines[newLines.Length - 1] == string.Empty)
                newLines = newLines[..^1];

            if (newLines.Length == 0)
                return "Error: 'contents' is empty — no lines to insert.";

            JsonNode? idxNode = input["line_index"];
            if (idxNode == null) return "Error: 'line_index' parameter is missing.";

            if (!ToolsIOHelpers.TryGetInt(idxNode, out int index))
                return "Error: 'line_index' must be a valid integer.";

            if (index < 0)
                return $"Error: 'line_index' must be >= 0, but got {index}.";

            if (index > lines.Count)
                return $"Error: 'line_index' ({index}) is out of range. " +
                       $"File has {lines.Count} line(s), valid insert range is 0 – {lines.Count}.";

            int oldLineCount = lines.Count;
            lines.InsertRange(index, newLines);

            err = ToolsIOHelpers.WriteLines(path, lines, hadTrailingNewline);
            if (err != null) return err;

            string summary = newLines.Length == 1
                ? $"Line inserted at index {index}. File now has {lines.Count} line(s)."
                : $"Inserted {newLines.Length} line(s) at index {index}. File now has {lines.Count} line(s).";

            // Shared builder (Tools.IO.cs): context lines + shift note + "saved" note,
            // and records what was shown in the per-round FileViewLedger.
            return BuildMutationResult(
                "insert_line", path, summary, null, lines,
                regionStart:  index,
                regionEnd:    index + newLines.Length - 1,
                contextLines: ResolveContextLines(input),
                oldLineCount: oldLineCount);
        }
    }
}
