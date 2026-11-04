using System;
using System.IO;
using System.Text.Json.Nodes;
using GTA.Tools.Impl.IO;

namespace GTA.Tools
{
    internal static partial class ToolsIO
    {
        [ToolTag("essential", "bootstrap")]
        [FileMutating]
        [ToolInfo(
            name: "write_file",
            description: "Write text content to a file at the given path on the local Windows machine. " +
                         "Creates the file and any missing parent directories. " +
                         "Overwrites the file if it already exists. Writes plain UTF-8 WITHOUT a " +
                         "byte-order-mark (BOM) by default, matching standard behavior for source-code/" +
                         "text files (C++, GLSL, JSON, etc. universally expect no BOM) — pass 'add_bom' " +
                         "= true to explicitly opt into a leading BOM instead.")]
        [Parameter(index: 0, name: "path",
            type: "string",
            description: "Full absolute Windows path of the file to write (e.g. C:\\project\\notes.txt).",
            required: true)]
        [Parameter(index: 1, name: "content",
            type: "string",
            description: "Text content to write into the file (UTF-8).",
            required: true)]
        [Parameter(index: 2, name: "add_bom",
            type: "boolean",
            description: "Optional. If true, writes a UTF-8 byte-order-mark (BOM, bytes EF BB BF) at the " +
                         "very start of the file. Defaults to false (no BOM), which is what almost every " +
                         "text/source-code consumer (compilers, shader tools, JSON parsers, etc.) expects. " +
                         "Only set this to true if a specific downstream consumer actually requires a BOM.",
            required: false)]
        public static object WriteFile(JsonObject input)
        {
            string? rawPath = input["path"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(rawPath))
                return "Error: 'path' parameter is missing or empty.";

            string path = ToolsIOHelpers.CleanPath(rawPath);

            string? content = input["content"]?.GetValue<string>();
            if (content == null)
                return "Error: 'content' parameter is missing.";

            bool addBom = ToolsIOHelpers.GetOptionalBool(input, "add_bom", false);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                System.Text.Encoding encodingForSize = addBom ? FileIoUtils.Utf8WithBom : FileIoUtils.Utf8NoBom;
                long totalBytes = encodingForSize.GetPreamble().Length + encodingForSize.GetByteCount(content);

                ToolRegistry.ReportProgress($"Writing {FileIoUtils.FormatBytes(totalBytes)} to '{path}'…");

                string? writeErr = FileIoUtils.WriteAllText(path, content, addBom,
                    onProgress: (written, total) =>
                    {
                        if (total <= 0) return;
                        ToolRegistry.ReportProgress(
                            $"Writing… {FileIoUtils.FormatBytes(written)} / {FileIoUtils.FormatBytes(total)} " +
                            $"({written * 100.0 / total:F0}%)");
                    });
                if (writeErr != null)
                    return writeErr;

                ToolRegistry.ReportProgress($"✅ Wrote {FileIoUtils.FormatBytes(totalBytes)} in {sw.ElapsedMilliseconds}ms.");

                return $"File written successfully: {path}";
            }
            catch (UnauthorizedAccessException) { return $"Error: Access denied — cannot write to '{path}'."; }
            catch (IOException ex)              { return $"Error: IO error while writing '{path}': {ex.Message}"; }
            catch (Exception ex)                { return $"Error: Unexpected error while writing '{path}': {ex.Message}"; }
        }
    }
}
