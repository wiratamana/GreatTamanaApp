using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GTA.Tools
{
    internal static partial class ToolsIO
    {
        [ToolTag("directory")]
        [ToolInfo(
            name: "search_in_dir",
            description: "Search for a text pattern across all files (or files matching a glob filter) " +
                         "inside a directory. Returns a clean, compact listing grouped by file: " +
                         "the file's relative path on its own line, followed by '[line_number] line text' " +
                         "for each match. Long lines are truncated and results are capped " +
                         "(per-file and total) to keep the response small and easy to read for an LLM. " +
                         "Line numbers are 0-based (the first line of each file is index 0). " +
                         "'path' can also be a single FILE: then only that file is searched ('filter' and " +
                         "'recursive' are ignored). Text encodings: UTF-8 (with/without BOM), UTF-16/UTF-32 LE " +
                         "with BOM, and Shift-JIS (CP932) — a file that is not valid UTF-8 but is valid " +
                         "Shift-JIS is decoded as Shift-JIS automatically, so Japanese search text works; " +
                         "such files are marked '[Shift-JIS (CP932)]' in the result. In single-file mode a " +
                         "binary file, a file with NUL bytes, a file that is too large (>50 MB) or a file in " +
                         "an unsupported encoding returns a clear error instead of being silently skipped.")]
        [Parameter(index: 0, name: "path",
            type: "string",
            description: "Full absolute Windows path of the directory to search, or of a single file to " +
                         "search inside.",
            required: true)]
        [Parameter(index: 1, name: "content",
            type: "string",
            description: "The text or pattern to search for.",
            required: true)]
        [Parameter(index: 2, name: "filter",
            type: "string",
            description: "Glob file filter, e.g. '*.cs' or '*.txt'. Defaults to '*' (all files).",
            required: false)]
        [Parameter(index: 3, name: "recursive",
            type: "boolean",
            description: "If true (default), search all sub-directories as well.",
            required: false)]
        [Parameter(index: 4, name: "use_regex",
            type: "boolean",
            description: "If true, 'content' is treated as a .NET regular expression. Defaults to false.",
            required: false)]
        [Parameter(index: 5, name: "match_case",
            type: "boolean",
            description: "If true, matching is case-sensitive. Defaults to false (case-insensitive).",
            required: false)]
        [Parameter(index: 6, name: "max_matches_per_file",
            type: "integer",
            description: "Maximum number of matching lines to show per file. Extra matches are summarized " +
                         "as '... N more match(es) in this file'. Defaults to 20 in directory mode. In " +
                         "single-file mode it defaults to max_total_matches (200).",
            required: false)]
        [Parameter(index: 7, name: "max_total_matches",
            type: "integer",
            description: "Maximum total matching lines to show across all files before stopping the search " +
                         "early. Defaults to 200.",
            required: false)]
        [Parameter(index: 8, name: "max_line_length",
            type: "integer",
            description: "Matching lines longer than this are truncated with '…'. Defaults to 300 characters.",
            required: false)]
        public static object SearchInDir(JsonObject input)
        {
            // 'path' may be a directory (search all files in it) or a single file (search just that file).
            string? rawPath = input["path"]?.GetValue<string>();
            string? filePath = null;
            string dirPath = string.Empty;
            if (!string.IsNullOrWhiteSpace(rawPath) && File.Exists(ToolsIOHelpers.CleanPath(rawPath)))
            {
                filePath = ToolsIOHelpers.CleanPath(rawPath);
            }
            else
            {
                string? err = ToolsIOHelpers.ResolveExistingDir(input, "path", out dirPath);
                if (err != null) return err;
            }

            string? query = input["content"]?.GetValue<string>();
            if (string.IsNullOrEmpty(query))
                return "Error: 'content' parameter is missing or empty.";

            string filter    = input["filter"]?.GetValue<string>()     ?? "*";
            bool   recursive = ToolsIOHelpers.GetOptionalBool(input, "recursive", true);
            bool   useRegex  = ToolsIOHelpers.GetOptionalBool(input, "use_regex", false);
            bool   matchCase = ToolsIOHelpers.GetOptionalBool(input, "match_case", false);

            int maxPerFile = ToolsIOHelpers.TryGetInt(input["max_matches_per_file"], out int mpf) ? mpf : 20;
            if (maxPerFile <= 0) maxPerFile = 20;

            int maxTotal = ToolsIOHelpers.TryGetInt(input["max_total_matches"], out int mt) ? mt : 200;
            if (maxTotal <= 0) maxTotal = 200;

            int maxLineLen = ToolsIOHelpers.TryGetInt(input["max_line_length"], out int mll) ? mll : 300;
            if (maxLineLen <= 0) maxLineLen = 300;

            string pattern = useRegex ? query : Regex.Escape(query);
            RegexOptions options = matchCase ? RegexOptions.None : RegexOptions.IgnoreCase;

            Regex regex;
            try
            {
                regex = new Regex(pattern, options, TimeSpan.FromSeconds(5));
            }
            catch (ArgumentException ex)
            {
                return $"Error: Invalid regex pattern — {ex.Message}";
            }

            // 'path' points to a FILE: search just that file (see SearchSingleFile).
            if (filePath != null)
            {
                // One file: use the per-file limit if the caller gave one, else the total limit.
                int fileLimit = input["max_matches_per_file"] != null ? maxPerFile : maxTotal;
                return SearchSingleFile(filePath, query, regex, Math.Min(fileLimit, maxTotal), maxLineLen);
            }

            SearchOption searchOption = recursive
                ? SearchOption.AllDirectories
                : SearchOption.TopDirectoryOnly;

            string[] files;
            try
            {
                files = Directory.GetFiles(dirPath, filter, searchOption);
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            }
            catch (UnauthorizedAccessException) { return $"Error: Access denied while enumerating '{dirPath}'."; }
            catch (IOException ex)              { return $"Error: IO error while enumerating '{dirPath}': {ex.Message}"; }
            catch (Exception ex)                { return $"Error: Unexpected error while enumerating '{dirPath}': {ex.Message}"; }

            if (files.Length == 0)
                return $"No files found in '{dirPath}' matching filter '{filter}'.";

            var sb         = new StringBuilder();
            int totalFiles = 0;
            int totalHits  = 0;
            bool truncatedGlobally = false;

            const long MAX_SEARCH_FILE_BYTES = 50L * 1024 * 1024; // 50 MB

            foreach (string file in files)
            {
                if (totalHits >= maxTotal)
                {
                    truncatedGlobally = true;
                    break;
                }

                // Cheap check first: known binary extensions never get opened as text.
                if (ToolsIOHelpers.IsBinaryExtension(file))
                    continue;

                long fileLen;
                try { fileLen = new FileInfo(file).Length; }
                catch { continue; }

                if (fileLen == 0 || fileLen > MAX_SEARCH_FILE_BYTES)
                    continue;

                // Fallback for binary files with no recognizable extension: sniff for NUL bytes.
                if (ToolsIOHelpers.IsBinaryContent(file))
                    continue;

                string[] lines;
                string? decodedAs = null;   // set only when the file is NOT plain UTF-8 (e.g. Shift-JIS)
                try
                {
                    byte[] bytes = File.ReadAllBytes(file);
                    if (DecodeTextBytes(bytes, out string decoded, out string decName) == null)
                    {
                        lines = SplitLines(decoded);
                        if (decName == ShiftJisName) decodedAs = decName;
                    }
                    else
                    {
                        // Not clean text: keep the old behaviour (lossy UTF-8) so nothing that matched before is lost.
                        lines = File.ReadAllLines(file, Encoding.UTF8);
                    }
                }
                catch
                {
                    continue;
                }

                var fileHits = new List<(int idx, string text)>();
                int fileMatchCount = 0;

                for (int i = 0; i < lines.Length; i++)
                {
                    bool match;
                    try   { match = regex.IsMatch(lines[i]); }
                    catch (RegexMatchTimeoutException) { continue; }

                    if (!match) continue;

                    fileMatchCount++;

                    if (fileHits.Count < maxPerFile && totalHits + fileHits.Count < maxTotal)
                        fileHits.Add((i, lines[i]));
                }

                if (fileMatchCount == 0) continue;

                totalFiles++;
                totalHits += fileHits.Count;

                string rel = Path.GetRelativePath(dirPath, file);

                if (sb.Length > 0) sb.Append('\n');
                sb.Append(rel);
                if (decodedAs != null) sb.Append("  [").Append(decodedAs).Append(']');
                sb.Append('\n');

                foreach (var (idx, text) in fileHits)
                {
                    string line = text.Trim();
                    if (line.Length > maxLineLen)
                        line = line.Substring(0, maxLineLen) + "…";

                    sb.Append('[').Append(idx).Append("] ").Append(line).Append('\n');
                }

                int remaining = fileMatchCount - fileHits.Count;
                if (remaining > 0)
                    sb.Append("... ").Append(remaining).Append(" more match(es) in this file\n");
            }

            if (totalHits == 0)
                return $"No matches found for \"{query}\" in '{dirPath}' (filter: {filter}).";

            string header = $"Found {totalHits} match(es) in {totalFiles} file(s) for \"{query}\" (filter: {filter})" +
                             (truncatedGlobally ? $" — stopped early at max_total_matches={maxTotal}" : "") + ":\n\n";

            return header + sb.ToString();
        }

        // ── Single-file mode (path points to a file, not a directory) ──────────────

        private const long MAX_SINGLE_FILE_BYTES = 50L * 1024 * 1024; // 50 MB, same limit as directory mode

        /// <summary>
        /// Search ONE file. Unlike directory mode (which silently skips unreadable/binary files), here the user
        /// asked for this exact file, so every reason it cannot be searched is reported as an explicit error:
        /// binary type, NUL bytes, too large, locked/denied, or bytes that are not valid text.
        /// Supported text encodings: UTF-8 (with/without BOM), UTF-16 LE/BE (BOM), UTF-32 LE (BOM).
        /// </summary>
        private static string SearchSingleFile(
            string file, string query, Regex regex, int maxMatches, int maxLineLen)
        {
            string ext = Path.GetExtension(file);

            if (ToolsIOHelpers.IsBinaryExtension(file))
                return $"Error: '{file}' is not readable as text — '{ext}' is a known binary file type. " +
                       "Use a different tool for this file (e.g. load_image for images).";

            long len;
            try { len = new FileInfo(file).Length; }
            catch (Exception ex) { return $"Error: Cannot get the size of '{file}': {ex.Message}"; }

            if (len == 0)
                return $"'{file}' is empty (0 bytes) — nothing to search.";
            if (len > MAX_SINGLE_FILE_BYTES)
                return $"Error: '{file}' is too large to search ({ToolsIOHelpers.FormatBytes(len)}, " +
                       $"limit {ToolsIOHelpers.FormatBytes(MAX_SINGLE_FILE_BYTES)}).";

            byte[] bytes;
            try
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var ms = new MemoryStream((int)Math.Min(len, int.MaxValue));
                fs.CopyTo(ms);
                bytes = ms.ToArray();
            }
            catch (UnauthorizedAccessException) { return $"Error: Access denied — cannot read '{file}'."; }
            catch (IOException ex)              { return $"Error: IO error while reading '{file}': {ex.Message}"; }
            catch (Exception ex)                { return $"Error: Unexpected error while reading '{file}': {ex.Message}"; }

            string? decodeError = DecodeTextBytes(bytes, out string text, out string encName);
            if (decodeError != null)
                return $"Error: '{file}' is not readable as text — {decodeError} " +
                       "Use detect_encoding to check the file.";

            var hits = new List<(int idx, string text)>();
            int matchCount = 0;
            int lineNo = 0;
            using (var reader = new StringReader(text))
            {
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    bool match;
                    try   { match = regex.IsMatch(line); }
                    catch (RegexMatchTimeoutException) { lineNo++; continue; }

                    if (match)
                    {
                        matchCount++;
                        if (hits.Count < maxMatches) hits.Add((lineNo, line));
                    }
                    lineNo++;
                }
            }

            if (matchCount == 0)
                return $"No matches found for \"{query}\" in file '{file}' ({lineNo} line(s), {encName}).";

            var sb = new StringBuilder();
            sb.Append($"Found {matchCount} match(es) in file '{file}' for \"{query}\" ({lineNo} line(s), {encName}):\n\n");
            foreach (var (idx, lineText) in hits)
            {
                string t = lineText.Trim();
                if (t.Length > maxLineLen)
                    t = t.Substring(0, maxLineLen) + "…";
                sb.Append('[').Append(idx).Append("] ").Append(t).Append('\n');
            }

            int remaining = matchCount - hits.Count;
            if (remaining > 0)
                sb.Append("... ").Append(remaining).Append(" more match(es) in this file\n");

            return sb.ToString();
        }

        // ── Text decoding (shared by file mode and directory mode) ─────────────────

        private const string ShiftJisName = "Shift-JIS (CP932)";

        // Strict Shift-JIS (CP932 = Windows-31J, what Japanese Windows apps write). .NET needs the code-pages
        // provider registered once. null = not available on this machine.
        private static readonly Lazy<Encoding?> StrictShiftJis = new(() =>
        {
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            }
            catch { return null; }
        });

        /// <summary>
        /// Decode file bytes as text. Order: BOM (UTF-8 / UTF-16 / UTF-32 LE) → strict UTF-8 → strict Shift-JIS.
        /// Returns null on success (text + encName are set), or a short reason why the bytes are not text.
        /// Without a BOM, NUL bytes in the first 8000 bytes mean binary. A Shift-JIS result with many control
        /// characters is treated as binary (random bytes can accidentally be valid Shift-JIS).
        /// </summary>
        private static string? DecodeTextBytes(byte[] bytes, out string text, out string encName)
        {
            text = string.Empty;
            encName = string.Empty;

            Encoding? bomEnc = null;
            int skip = 0;
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            { bomEnc = new UTF8Encoding(false, true); skip = 3; encName = "UTF-8 (BOM)"; }
            else if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0 && bytes[3] == 0)
            { bomEnc = new UTF32Encoding(false, false, true); skip = 4; encName = "UTF-32 LE"; }
            else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            { bomEnc = new UnicodeEncoding(false, false, true); skip = 2; encName = "UTF-16 LE"; }
            else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            { bomEnc = new UnicodeEncoding(true, false, true); skip = 2; encName = "UTF-16 BE"; }

            if (bomEnc != null)
            {
                try
                {
                    text = bomEnc.GetString(bytes, skip, bytes.Length - skip);
                    return null;
                }
                catch (DecoderFallbackException)
                {
                    return $"the bytes are not valid {encName}.";
                }
            }

            int sample = Math.Min(bytes.Length, 8000);
            for (int i = 0; i < sample; i++)
            {
                if (bytes[i] == 0)
                    return "it contains NUL bytes (binary content, or UTF-16/32 without a BOM).";
            }

            try
            {
                text = new UTF8Encoding(false, true).GetString(bytes);
                encName = "UTF-8";
                return null;
            }
            catch (DecoderFallbackException) { /* try Shift-JIS below */ }

            Encoding? sjis = StrictShiftJis.Value;
            if (sjis != null)
            {
                try
                {
                    string s = sjis.GetString(bytes);
                    int ctrl = 0;
                    foreach (char c in s)
                    {
                        if ((c < 0x20 && c != '\t' && c != '\n' && c != '\r' && c != '\f') || c == 0x7F)
                            ctrl++;
                    }
                    if (ctrl * 100 <= s.Length)
                    {
                        text = s;
                        encName = ShiftJisName;
                        return null;
                    }
                }
                catch (DecoderFallbackException) { /* not Shift-JIS either */ }
            }

            return "the bytes are neither valid UTF-8 nor valid Shift-JIS (binary, or another encoding such as EUC-JP).";
        }

        /// <summary>Split text into lines the same way File.ReadAllLines does (\r\n, \n and \r all end a line).</summary>
        private static string[] SplitLines(string text)
        {
            var list = new List<string>();
            using var reader = new StringReader(text);
            string? line;
            while ((line = reader.ReadLine()) != null)
                list.Add(line);
            return list.ToArray();
        }
    }
}
