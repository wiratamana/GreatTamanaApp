using System;
using System.IO;
using System.Text.Json.Nodes;

namespace GTA.Tools
{
    internal static partial class ToolsIO
    {
        [ToolTag("io-specialist")]
        [ToolInfo(
            name: "detect_encoding",
            description: "Inspect the BOM (Byte Order Mark) and byte patterns of a file to identify " +
                         "its text encoding: UTF-8 (with/without BOM), UTF-16 LE/BE, UTF-32, ASCII, etc. " +
                         "Useful before reading files that may not be UTF-8 (e.g. registry exports, " +
                         "some CSV files, PowerShell output).")]
        [Parameter(index: 0, name: "path",
            type: "string",
            description: "Full absolute Windows path of the file to inspect.",
            required: true)]
        public static object DetectEncoding(JsonObject input)
        {
            string? err = ToolsIOHelpers.ResolveExistingFile(input, "path", out string path);
            if (err != null) return err;

            try
            {
                long fileSize = new FileInfo(path).Length;
                if (fileSize == 0)
                    return $"File is empty (0 bytes): '{path}'. Encoding cannot be determined.";

                int    readCount = (int)Math.Min(fileSize, 4096);
                byte[] sample    = new byte[readCount];

                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    fs.ReadExactly(sample, 0, readCount);

                if (sample.Length >= 4)
                {
                    if (sample[0] == 0xFF && sample[1] == 0xFE && sample[2] == 0x00 && sample[3] == 0x00)
                        return "UTF-32 LE (BOM present)";
                    if (sample[0] == 0x00 && sample[1] == 0x00 && sample[2] == 0xFE && sample[3] == 0xFF)
                        return "UTF-32 BE (BOM present)";
                }
                if (sample.Length >= 3)
                {
                    if (sample[0] == 0xEF && sample[1] == 0xBB && sample[2] == 0xBF)
                        return "UTF-8 (BOM present)";
                }
                if (sample.Length >= 2)
                {
                    if (sample[0] == 0xFF && sample[1] == 0xFE)
                        return "UTF-16 LE (BOM present)";
                    if (sample[0] == 0xFE && sample[1] == 0xFF)
                        return "UTF-16 BE (BOM present)";
                }

                int nullsOdd  = 0;
                int nullsEven = 0;
                int pairs     = sample.Length / 2;

                for (int i = 0; i + 1 < sample.Length; i += 2)
                {
                    if (sample[i]     == 0x00) nullsEven++;
                    if (sample[i + 1] == 0x00) nullsOdd++;
                }

                if (pairs > 0)
                {
                    if ((double)nullsOdd  / pairs > 0.60)
                        return "UTF-16 LE (no BOM — heuristic, may be unreliable for binary files)";
                    if ((double)nullsEven / pairs > 0.60)
                        return "UTF-16 BE (no BOM — heuristic, may be unreliable for binary files)";
                }

                bool hasHighBytes = false;
                bool isValidUtf8  = true;
                int  k            = 0;

                while (k < sample.Length)
                {
                    byte b = sample[k];
                    if (b <= 0x7F) { k++; continue; }

                    hasHighBytes = true;

                    int extra;
                    if      ((b & 0xE0) == 0xC0) extra = 1;
                    else if ((b & 0xF0) == 0xE0) extra = 2;
                    else if ((b & 0xF8) == 0xF0) extra = 3;
                    else   { isValidUtf8 = false; break; }

                    bool valid = true;
                    for (int j = 1; j <= extra; j++)
                    {
                        if (k + j >= sample.Length || (sample[k + j] & 0xC0) != 0x80)
                        {
                            valid = false;
                            break;
                        }
                    }

                    if (!valid) { isValidUtf8 = false; break; }
                    k += 1 + extra;
                }

                if (!hasHighBytes)
                    return "ASCII (all bytes ≤ 0x7F — fully compatible with UTF-8 / ASCII)";

                if (isValidUtf8)
                    return "UTF-8 (no BOM — valid multi-byte sequences detected in sample)";

                return "Unknown / Latin-1 (ISO-8859-1) or binary content " +
                       "— contains byte sequences that are invalid UTF-8";
            }
            catch (UnauthorizedAccessException) { return $"Error: Access denied — cannot read '{path}'."; }
            catch (IOException ex)              { return $"Error: IO error while reading '{path}': {ex.Message}"; }
            catch (Exception ex)                { return $"Error: Unexpected error while inspecting '{path}': {ex.Message}"; }
        }
    }
}
