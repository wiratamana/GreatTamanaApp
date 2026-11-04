using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;

namespace GTA.Tools
{
    internal static partial class ToolsIO
    {
        [ToolTag("essential", "bootstrap")]
        [ToolInfo(
            name: "read_line",
            description: "Read lines from a text file and return them with their 0-based index in [NNN] format. " +
                         "Optionally specify a 0-based start line index and how many lines to read. " +
                         "A context margin of 5 lines is automatically shown before and after the requested range. " +
                         "Padding uses 3 digits for files under 1 000 lines, 4 digits for 1 000 or more. " +
                         "Line numbers are 0-based (the first line of the file is index 0). " +
                         "Do NOT use this right after edit_line/insert_line/remove_line just to verify the edit — " +
                         "their result already contains the final lines; if every requested line was already returned " +
                         "earlier in the same round and the file is unchanged, this tool answers with a short " +
                         "'[Not repeated]' pointer instead of the text.")]
        [Parameter(index: 0, name: "path",
            type: "string",
            description: "Full absolute Windows path of the text file to read.",
            required: true)]
        [Parameter(index: 1, name: "start",
            type: "integer",
            description: "0-based line index to start reading from. Defaults to 0 (the very first line).",
            required: false)]
        [Parameter(index: 2, name: "length",
            type: "integer",
            description: "How many lines to read. Defaults to all remaining lines from 'start'. " +
                         "Silently clamped if it would go past the end of the file.",
            required: false)]
        public static object ReadLine(JsonObject input)
        {
            string? err = ToolsIOHelpers.ResolvePath(input, out string path);
            if (err != null) return err;

            err = ToolsIOHelpers.ReadLines(path, out List<string> lineList, out _);
            if (err != null) return err;

            string[] allLines   = lineList.ToArray();
            int      totalLines = allLines.Length;

            if (totalLines == 0)
                return $"(File is empty: {path})";

            int start = 0;
            JsonNode? startNode = input["start"];
            if (startNode != null)
            {
                if (!ToolsIOHelpers.TryGetInt(startNode, out start))
                    return "Error: 'start' must be a valid integer (e.g. 1, 10, 100).";

                if (start < 0)
                    return $"Error: 'start' must be >= 0, but got {start}.";
                if (start >= totalLines)
                    return $"Error: 'start' ({start}) is out of range. File has {totalLines} line(s), valid range is 0 – {totalLines - 1}.";
            }

            int length = totalLines - start;
            JsonNode? lengthNode = input["length"];
            if (lengthNode != null)
            {
                if (!ToolsIOHelpers.TryGetInt(lengthNode, out length))
                    return "Error: 'length' must be a valid integer (e.g. 10, 50).";

                if (length < 1)
                    return $"Error: 'length' must be >= 1, but got {length}.";

                if (length > int.MaxValue - start)
                    length = totalLines - start;
            }

            int lastIdx = Math.Min(start + length - 1, totalLines - 1);

            FileViewLedger? ledger = FileViewLedger.Current;
            if (ledger != null && ledger.TryGetCovering(path, start, lastIdx, out string shownBy))
            {
                return $"[Not repeated] Lines {start}–{lastIdx} of '{path}' were already returned by {shownBy} " +
                       "earlier in this same round, and the file has not changed since. See that result above. " +
                       "(Need a fresh copy anyway? Request it again in the next round.)";
            }

            // Add 5-line context margin before and after the requested range
            int showStart = Math.Max(0, start - 5);
            int showEnd   = Math.Min(totalLines - 1, lastIdx + 5);

            int padWidth = Math.Max(totalLines >= 1000 ? 4 : 3, showEnd.ToString().Length);
            var sb       = new StringBuilder((showEnd - showStart + 1) * 80);

            for (int i = showStart; i <= showEnd; i++)
            {
                sb.Append('[');
                sb.Append(i.ToString().PadLeft(padWidth, '0'));
                sb.Append("] ");
                sb.AppendLine(allLines[i]);
            }

            ledger?.RecordShown(path, showStart, showEnd, totalLines, "read_line", replaceExisting: false);
            return sb.ToString();
        }
    }
}
