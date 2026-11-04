using System;
using System.Text;
using System.Text.Json.Nodes;

namespace GTA.Tools
{
    internal static partial class ToolsIO
    {
        [ToolTag("essential", "bootstrap", "io-specialist")]
        [FileMutating]
        [ToolInfo(
            name: "edit_line",
            description: "Atomically replace one or more consecutive lines at the given 0-based index with new content. " +
                         "When 'length' is omitted it defaults to 1 (single-line replace, similar to lines[index] = newText). " +
                         "The replacement may have a different number of lines than the removed range. " +
                         "The result shows the replacement lines with a few context lines before and after them " +
                         "(default 2, see 'context_lines'), all numbered with their 0-based index in [NNN] format, " +
                         "and tells you how far the lines below moved. " +
                         "THE RESULT IS THE FINAL SAVED CONTENT of that region — do NOT call read_line/read_file " +
                         "afterwards just to verify the edit (a duplicate read of the same lines in the same round " +
                         "is skipped automatically). Very long replacement blocks are echoed back only at their " +
                         "first/last lines. " +
                         "Line numbers are 0-based (the first line of the file is index 0). " +
                         "POST-CHECK AUTO-DEDUP: after splicing, this tool checks BOTH boundaries of the newly " +
                         "inserted block for an exact, non-blank duplicate line (a common mistake when 'contents' " +
                         "redundantly repeats a line that already exists just outside the edited range, e.g. the " +
                         "caller meant to overwrite further but left 'length' at its default of 1) — if found, the " +
                         "leftover original duplicate is automatically removed and a note is added to the result " +
                         "so you can verify it. This only catches EXACT boundary duplicates; it is not a substitute " +
                         "for checking the result. " +
                         "CAUTION for FULL-FILE replacements: 'length' must match the file's CURRENT total line " +
                         "count at call time (minus line_index) — if the file actually has MORE lines than you " +
                         "assumed (e.g. it changed since you last checked, or you miscounted), any lines beyond " +
                         "'length' are left untouched and will remain appended after your new content, which can " +
                         "silently produce a malformed/duplicated tail (e.g. a duplicated closing tag). Always " +
                         "re-check the line count immediately before the call (e.g. via get_file_line_count or a " +
                         "fresh read), and inspect the context in this tool's own result to confirm " +
                         "no unexpected leftover lines remain. When the intent is to replace an ENTIRE file's " +
                         "contents, prefer 'write_file' instead — it overwrites the whole file directly and does " +
                         "not require knowing the line count at all, eliminating this failure mode entirely.")]
        [Parameter(index: 0, name: "path",
            type: "string",
            description: "Full absolute Windows path of the file to modify.",
            required: true)]
        [Parameter(index: 1, name: "contents",
            type: "string",
            description: "The replacement line(s), separated by '\\n'. Each segment becomes one new line. " +
                         "Pass an empty string to delete the range without inserting anything.",
            required: true)]
        [Parameter(index: 2, name: "line_index",
            type: "integer",
            description: "0-based index of the (first) line to replace. Valid range: 0 to (line count - 1).",
            required: true)]
        [Parameter(index: 3, name: "length",
            type: "integer",
            description: "Number of lines to remove starting at line_index. Must be >= 1. Defaults to 1. " +
                         "If 'length' extends beyond the last line of the file, it is silently clamped to " +
                         "however many lines actually remain (safe to over-estimate). It is NEVER extended if " +
                         "under-estimated, though — if the file has more remaining lines than 'length' accounts " +
                         "for, those extra original lines are kept and end up appended after the new content. " +
                         "For a guaranteed full-file replace, pass a deliberately large 'length' (e.g. 999999) " +
                         "or, better, use 'write_file' instead of this tool.",
            required: false)]
        [Parameter(index: 4, name: "preserve_trailing_newline",
            type: "boolean",
            description: "If true, a trailing blank line at the end of 'contents' is kept as an " +
                         "explicit blank line. By default (false) a trailing '\\n' in 'contents' does " +
                         "not insert an extra blank line.",
            required: false)]
        [Parameter(index: 5, name: "context_lines",
            type: "integer",
            description: "How many unchanged lines to show before and after the edited region in the result. " +
                         "Default 2, range 0-20.",
            required: false)]
        public static object EditLine(JsonObject input)
        {
            string? err = ToolsIOHelpers.ResolveExistingFile(input, "path", out string path);
            if (err != null) return err;

            err = ToolsIOHelpers.ReadLines(path, out System.Collections.Generic.List<string> lines, out bool hadTrailingNewline);
            if (err != null) return err;

            if (lines.Count == 0)
                return "Error: File is empty — nothing to edit.";
            int oldLineCount = lines.Count; // for the "lines below moved by N" note

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

            int actualRemove = Math.Min(length, lines.Count - index);

            string? raw = input["contents"]?.GetValue<string>();
            if (raw == null)
                return "Error: 'contents' parameter is missing.";

            string[] newLines = Array.Empty<string>();
            if (raw.Length > 0)
            {
                newLines = raw.Split('\n');
                for (int i = 0; i < newLines.Length; i++)
                    newLines[i] = newLines[i].TrimEnd('\r');

                bool preserveTrailing = ToolsIOHelpers.GetOptionalBool(input, "preserve_trailing_newline", false);
                if (!preserveTrailing && newLines.Length > 0 && newLines[newLines.Length - 1] == string.Empty)
                    newLines = newLines[..^1];
            }

            lines.RemoveRange(index, actualRemove);
            if (newLines.Length > 0)
                lines.InsertRange(index, newLines);

            bool dedupedAfter  = false;
            bool dedupedBefore = false;

            if (newLines.Length > 0)
            {
                int afterIdx = index + newLines.Length;
                if (afterIdx < lines.Count &&
                    lines[afterIdx - 1].Trim().Length > 0 &&
                    lines[afterIdx] == lines[afterIdx - 1])
                {
                    lines.RemoveAt(afterIdx);
                    dedupedAfter = true;
                }

                if (index > 0 &&
                    lines[index].Trim().Length > 0 &&
                    lines[index - 1] == lines[index])
                {
                    lines.RemoveAt(index - 1);
                    dedupedBefore = true;
                    index--; // the inserted block shifted down by one
                }
            }

            err = ToolsIOHelpers.WriteLines(path, lines, hadTrailingNewline);
            if (err != null) return err;

            string summary = $"Replaced {actualRemove} line(s) at index {index} with {newLines.Length} new line(s). " +
                             $"File now has {lines.Count} line(s).";

            string? notes = null;
            if (dedupedAfter)
                notes = "NOTE: auto-dedup removed 1 leftover line immediately AFTER the inserted " +
                        "content that exactly duplicated the last inserted line — this usually means " +
                        "'contents' accidentally overlapped with pre-existing text just past the " +
                        "edited range. Please verify the result below is what you intended.";
            if (dedupedBefore)
                notes = (notes == null ? "" : notes + Environment.NewLine) +
                        "NOTE: auto-dedup removed 1 leftover line immediately BEFORE the inserted " +
                        "content that exactly duplicated the first inserted line — this usually means " +
                        "'contents' accidentally overlapped with pre-existing text just before the " +
                        "edited range. Please verify the result below is what you intended.";

            return BuildMutationResult(
                "edit_line", path, summary, notes, lines,
                regionStart:  index,
                regionEnd:    index + Math.Max(newLines.Length - 1, 0),
                contextLines: ResolveContextLines(input),
                oldLineCount: oldLineCount);
        }
    }
}
