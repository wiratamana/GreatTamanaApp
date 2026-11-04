using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace GTA.Tools
{
    internal static partial class ToolsIO
    {
        [ToolTag("essential")]
        [FileMutating]
        [ToolInfo(
            name: "append_to_file",
            description: "Append text to the end of a file without overwriting it. " +
                         "Creates the file (and any missing parent directories) if it does not exist. " +
                         "When 'newline' is true (default) and the file is non-empty, a newline is " +
                         "inserted before the content so it starts on its own line. " +
                         "The result shows the appended lines with a few context lines before them " +
                         "(default 2, see 'context_lines'), all numbered with their 0-based index in [NNN] format. " +
                         "THE RESULT IS FINAL — do NOT call read_line/read_file afterwards just to verify the " +
                         "append. Very long appended blocks are echoed back only at their first/last lines. " +
                         "Line numbers are 0-based (the first line of the file is index 0).")]
        [Parameter(index: 0, name: "path",
            type: "string",
            description: "Full absolute Windows path of the file to append to.",
            required: true)]
        [Parameter(index: 1, name: "content",
            type: "string",
            description: "Text to append to the file (UTF-8).",
            required: true)]
        [Parameter(index: 2, name: "newline",
            type: "boolean",
            description: "If true (default), a newline is prepended when the file already has content, " +
                         "ensuring the appended text begins on a fresh line.",
            required: false)]
        [Parameter(index: 3, name: "context_lines",
            type: "integer",
            description: "How many existing lines to show before the appended content in the result. " +
                         "Default 2, range 0-20.",
            required: false)]
        public static object AppendToFile(JsonObject input)
        {
            string? rawPath = input["path"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(rawPath))
                return "Error: 'path' parameter is missing or empty.";

            string path = ToolsIOHelpers.CleanPath(rawPath);

            if (Directory.Exists(path))
                return $"Error: '{path}' is a directory, not a file.";

            string? content = input["content"]?.GetValue<string>();
            if (content == null)
                return "Error: 'content' parameter is missing.";

            bool newline = ToolsIOHelpers.GetOptionalBool(input, "newline", true);

            // Read the last few lines for context (default 2, see 'context_lines') and
            // count existing lines for 0-based reporting
            int      contextLines = ResolveContextLines(input);
            int      linesBefore  = 0;
            string[] preContext   = Array.Empty<string>();
            if (File.Exists(path))
            {
                string? readErr = ToolsIOHelpers.ReadLines(path, out List<string> preLines, out _);
                if (readErr == null)
                {
                    linesBefore = preLines.Count;
                    int ctxIdx  = Math.Max(0, linesBefore - contextLines);
                    preContext   = preLines.GetRange(ctxIdx, linesBefore - ctxIdx).ToArray();
                }
            }

            try
            {
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                long bytesWritten;
                long newSize;

                {
                    using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None);
                    using var sw = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

                    if (newline && fs.Position > 0)
                    {
                        sw.Write('\n');
                        sw.Flush();
                    }

                    long posBefore = fs.Position;
                    sw.Write(content);
                    sw.Flush();
                    bytesWritten = fs.Position - posBefore;
                    newSize      = fs.Position;
                } // stream is closed here before we display context

                // Split content into lines and display with their 0-based file indices
                string[] appendedLines = content.Split('\n');
                for (int i = 0; i < appendedLines.Length; i++)
                    appendedLines[i] = appendedLines[i].TrimEnd('\r');

                int totalAfter = linesBefore + appendedLines.Length;
                int padWidth   = Math.Max(totalAfter >= 1000 ? 4 : 3, (totalAfter - 1).ToString().Length);

                var sbAppend = new StringBuilder();
                sbAppend.Append($"Appended {bytesWritten} byte(s) to '{path}' " +
                                $"(content: {content.Length} char(s); file is now {newSize} bytes).");
                sbAppend.AppendLine();

                // Show the short context margin (default 2 lines) before the appended content
                int ctxBaseIdx = Math.Max(0, linesBefore - contextLines);
                for (int i = 0; i < preContext.Length; i++)
                {
                    int lineIdx = ctxBaseIdx + i;
                    sbAppend.Append('[');
                    sbAppend.Append(lineIdx.ToString().PadLeft(padWidth, '0'));
                    sbAppend.Append("] ");
                    sbAppend.AppendLine(preContext[i]);
                }

                // Very long appended blocks: echo only the first/last lines — the middle
                // is exactly what the model itself just sent (see ToolsIO.ElideBlockThreshold).
                bool elide = appendedLines.Length > ElideBlockThreshold;
                for (int i = 0; i < appendedLines.Length; i++)
                {
                    if (elide && i == ElideKeepEachSide)
                    {
                        int skipTo = appendedLines.Length - ElideKeepEachSide;
                        sbAppend.AppendLine($"      … {skipTo - i} line(s) [{linesBefore + i}–{linesBefore + skipTo - 1}] omitted — " +
                                            "they are exactly the lines you just wrote …");
                        i = skipTo - 1;
                        continue;
                    }

                    int lineIdx = linesBefore + i;
                    sbAppend.Append('[');
                    sbAppend.Append(lineIdx.ToString().PadLeft(padWidth, '0'));
                    sbAppend.Append("] ");
                    sbAppend.Append(appendedLines[i]);
                    if (i < appendedLines.Length - 1) sbAppend.AppendLine();
                }

                sbAppend.AppendLine();
                sbAppend.Append("(Saved. No need to call read_line/read_file to verify the appended lines.)");

                return sbAppend.ToString();
            }
            catch (UnauthorizedAccessException) { return $"Error: Access denied — cannot write to '{path}'."; }
            catch (IOException ex)              { return $"Error: IO error while appending to '{path}': {ex.Message}"; }
            catch (Exception ex)                { return $"Error: Unexpected error while appending to '{path}': {ex.Message}"; }
        }
    }
}
