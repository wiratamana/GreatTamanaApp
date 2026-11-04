using System;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace GTA.Tools
{
    internal static partial class ToolsIO
    {
        [ToolTag("essential", "bootstrap")]
        [ToolInfo(
            name: "read_file",
            description: "Read and return the full text content of a file from the local Windows machine. " +
                         "Each line is prefixed with its 0-based index in [NNN] format " +
                         "(3-digit padding for files under 1 000 lines, 4-digit for 1 000 or more). " +
                         "Line numbers are 0-based (the first line of the file is index 0).")]
        [Parameter(index: 0, name: "path",
            type: "string",
            description: "Full absolute Windows path of the file to read.",
            required: true)]
        public static object ReadFile(JsonObject input)
        {
            string? err = ToolsIOHelpers.ResolvePath(input, out string path);
            if (err != null) return err;

            try
            {
                long fileSize = new FileInfo(path).Length;
                if (fileSize > ToolsIOHelpers.MAX_READ_BYTES)
                    return $"Error: File is too large to read fully ({ToolsIOHelpers.FormatBytes(fileSize)}). " +
                           "Use read_line with start/length to read it in sections, " +
                           "or get_file_line_count to determine how many lines it has.";

                string[] allLines = File.ReadAllLines(path, Encoding.UTF8);
                int      total    = allLines.Length;

                if (total == 0)
                    return $"(File is empty: {path})";

                FileViewLedger? ledger = FileViewLedger.Current;
                if (ledger != null && ledger.TryGetCovering(path, 0, total - 1, out string shownBy))
                {
                    return $"[Not repeated] The whole of '{path}' ({total} line(s)) was already returned by {shownBy} " +
                           "earlier in this same round, and the file has not changed since. See that result above. " +
                           "(Need a fresh copy anyway? Request it again in the next round.)";
                }

                int padWidth = Math.Max(total >= 1000 ? 4 : 3, (total - 1).ToString().Length);
                var sb       = new StringBuilder((int)Math.Min(fileSize + 1024, (long)int.MaxValue));

                for (int i = 0; i < total; i++)
                {
                    sb.Append('[');
                    sb.Append(i.ToString().PadLeft(padWidth, '0'));
                    sb.Append("] ");
                    sb.AppendLine(allLines[i]);
                }

                ledger?.RecordShown(path, 0, total - 1, total, "read_file", replaceExisting: false);
                return sb.ToString();
            }
            catch (UnauthorizedAccessException)  { return $"Error: Access denied \u2014 cannot read '{path}'."; }
            catch (IOException ex)               { return $"Error: IO error while reading '{path}': {ex.Message}"; }
            catch (Exception ex)                 { return $"Error: Unexpected error while reading '{path}': {ex.Message}"; }
        }
    }
}
