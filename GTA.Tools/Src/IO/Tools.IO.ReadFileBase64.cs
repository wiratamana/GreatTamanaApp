using System;
using System.IO;
using System.Text.Json.Nodes;

namespace GTA.Tools
{
    internal static partial class ToolsIO
    {
        [ToolTag("io-specialist")]
        [ToolInfo(
            name: "read_file_base64",
            description: "Read any file (text or binary) and return its content as a Base64-encoded string. " +
                         "Useful for images, compiled executables, PDFs, or any binary file " +
                         "whose bytes must be represented as safe text. " +
                         "Limited to files ≤ 10 MB to avoid excessively large outputs.")]
        [Parameter(index: 0, name: "path",
            type: "string",
            description: "Full absolute Windows path of the file to read.",
            required: true)]
        public static object ReadFileBase64(JsonObject input)
        {
            string? err = ToolsIOHelpers.ResolveExistingFile(input, "path", out string path);
            if (err != null) return err;

            const long MAX_BASE64_BYTES = 10L * 1024 * 1024; // 10 MB

            try
            {
                long fileSize = new FileInfo(path).Length;
                if (fileSize > MAX_BASE64_BYTES)
                    return $"Error: File is too large for Base64 output ({ToolsIOHelpers.FormatBytes(fileSize)}). " +
                           "Maximum allowed size is 10 MB.";

                byte[] bytes  = File.ReadAllBytes(path);
                string base64 = Convert.ToBase64String(bytes);
                return base64;
            }
            catch (UnauthorizedAccessException) { return $"Error: Access denied — cannot read '{path}'."; }
            catch (IOException ex)              { return $"Error: IO error while reading '{path}': {ex.Message}"; }
            catch (Exception ex)                { return $"Error: Unexpected error while reading '{path}': {ex.Message}"; }
        }
    }
}
