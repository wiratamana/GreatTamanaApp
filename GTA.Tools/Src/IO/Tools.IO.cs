using System.Text.Json.Nodes;

namespace GTA.Tools
{
    internal static partial class ToolsIO
    {
        // Shared result shaping for write tools (edit_line / insert_line / remove_line / append_to_file).
        /// <summary>Default number of context lines shown on each side of an edited region.</summary>
        internal const int DefaultContextLines = 2;

        /// <summary>Upper bound for the optional 'context_lines' parameter.</summary>
        internal const int MaxContextLines = 20;

        internal const int ElideBlockThreshold = 40;

        /// <summary>Lines kept at each end of an elided block.</summary>
        internal const int ElideKeepEachSide = 10;

        internal static int ResolveContextLines(JsonObject input)
        {
            JsonNode? node = input["context_lines"];
            if (node != null && ToolsIOHelpers.TryGetInt(node, out int v))
                return Math.Clamp(v, 0, MaxContextLines);
            return DefaultContextLines;
        }

        private static void AppendNumberedLines(
            System.Text.StringBuilder sb,
            System.Collections.Generic.List<string> lines,
            int from, int to, int padWidth)
        {
            for (int i = from; i <= to; i++)
            {
                sb.Append('[');
                sb.Append(i.ToString().PadLeft(padWidth, '0'));
                sb.Append("] ");
                sb.AppendLine(lines[i]);
            }
        }

        internal static string BuildMutationResult(
            string toolName,
            string path,
            string summary,
            string? notes,
            System.Collections.Generic.List<string> lines,
            int regionStart,
            int regionEnd,
            int contextLines,
            int oldLineCount)
        {
            var sb    = new System.Text.StringBuilder();
            int count = lines.Count;
            int delta = count - oldLineCount;

            sb.Append(summary);
            if (delta != 0)
                sb.Append($" Lines below the edit moved by {delta:+0;-0}.");
            sb.AppendLine();

            if (!string.IsNullOrEmpty(notes))
                sb.AppendLine(notes);

            if (count == 0)
            {
                sb.AppendLine("(File is now empty.)");
                return sb.ToString();
            }

            regionStart = Math.Clamp(regionStart, 0, count - 1);
            regionEnd   = Math.Clamp(regionEnd, regionStart, count - 1);

            int showStart = Math.Max(0, regionStart - contextLines);
            int showEnd   = Math.Min(count - 1, regionEnd + contextLines);
            int padWidth  = Math.Max(count >= 1000 ? 4 : 3, (count - 1).ToString().Length);

            FileViewLedger? ledger = FileViewLedger.Current;
            bool elide = (regionEnd - regionStart + 1) > ElideBlockThreshold;

            if (!elide)
            {
                AppendNumberedLines(sb, lines, showStart, showEnd, padWidth);
                ledger?.RecordShown(path, showStart, showEnd, count, toolName, replaceExisting: true);
            }
            else
            {
                int headEnd   = regionStart + ElideKeepEachSide - 1;
                int tailStart = regionEnd - ElideKeepEachSide + 1;

                AppendNumberedLines(sb, lines, showStart, headEnd, padWidth);
                sb.AppendLine($"      … {tailStart - headEnd - 1} line(s) [{headEnd + 1}–{tailStart - 1}] omitted — " +
                              "they are exactly the lines you just wrote …");
                AppendNumberedLines(sb, lines, tailStart, showEnd, padWidth);

                ledger?.RecordShown(path, showStart, headEnd, count, toolName, replaceExisting: true);
                ledger?.RecordShown(path, tailStart, showEnd, count, toolName, replaceExisting: false);
            }

            sb.AppendLine("(Saved. The lines above are the final content of the edited region — " +
                          "no need to call read_line/read_file to verify it.)");
            return sb.ToString();
        }

        private static string? ResolveLineIndex(JsonObject input, int lineCount, out int index)
        {
            index = 0;

            JsonNode? node = input["line_index"];
            if (node == null)
                return "Error: 'line_index' parameter is missing.";

            if (!ToolsIOHelpers.TryGetInt(node, out index))
                return "Error: 'line_index' must be a valid integer.";

            if (index < 0)
                return $"Error: 'line_index' must be >= 0, but got {index}.";

            if (index >= lineCount)
                return $"Error: 'line_index' ({index}) is out of range. " +
                       $"File has {lineCount} line(s), valid range is 0 – {lineCount - 1}.";

            return null;
        }
    }
}
