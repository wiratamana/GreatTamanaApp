using System;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GTA.Tools
{
    internal static partial class ToolsIO
    {
        [ToolTag("io-specialist")]
        [FileMutating]
        [ToolInfo(
            name: "replace_in_file",
            description: "Find-and-replace occurrences of a string (or regex pattern) across the entire " +
                         "file content and save the result. " +
                         "Much more efficient than calling search_line + edit_line for every matched line. " +
                         "Returns the number of replacements made.")]
        [Parameter(index: 0, name: "path",
            type: "string",
            description: "Full absolute Windows path of the file to modify.",
            required: true)]
        [Parameter(index: 1, name: "old_text",
            type: "string",
            description: "The text (or regex pattern when use_regex=true) to search for.",
            required: true)]
        [Parameter(index: 2, name: "new_text",
            type: "string",
            description: "The replacement text. " +
                         "When use_regex=true, standard .NET substitution syntax applies (e.g. $1 for group 1).",
            required: true)]
        [Parameter(index: 3, name: "use_regex",
            type: "boolean",
            description: "If true, 'old_text' is treated as a .NET regular expression. Defaults to false.",
            required: false)]
        [Parameter(index: 4, name: "match_case",
            type: "boolean",
            description: "If true, matching is case-sensitive. Defaults to false (case-insensitive).",
            required: false)]
        [Parameter(index: 5, name: "max_count",
            type: "integer",
            description: "Maximum number of replacements to make. " +
                         "0 (default) means replace all occurrences.",
            required: false)]
        public static object ReplaceInFile(JsonObject input)
        {
            string? err = ToolsIOHelpers.ResolveExistingFile(input, "path", out string path);
            if (err != null) return err;

            string? oldText = input["old_text"]?.GetValue<string>();
            if (string.IsNullOrEmpty(oldText))
                return "Error: 'old_text' parameter is missing or empty.";

            string newText = input["new_text"]?.GetValue<string>() ?? string.Empty;

            bool useRegex  = ToolsIOHelpers.GetOptionalBool(input, "use_regex", false);
            bool matchCase = ToolsIOHelpers.GetOptionalBool(input, "match_case", false);
            int  maxCount  = 0;

            JsonNode? mcNode = input["max_count"];
            if (mcNode != null)
            {
                if (!ToolsIOHelpers.TryGetInt(mcNode, out maxCount))
                    return "Error: 'max_count' must be a valid integer.";
                if (maxCount < 0)
                    return $"Error: 'max_count' must be >= 0, but got {maxCount}.";
            }

            string pattern = useRegex ? oldText : Regex.Escape(oldText);
            RegexOptions options = matchCase ? RegexOptions.None : RegexOptions.IgnoreCase;

            Regex regex;
            try
            {
                regex = new Regex(pattern, options, TimeSpan.FromSeconds(10));
            }
            catch (ArgumentException ex)
            {
                return $"Error: Invalid regex pattern — {ex.Message}";
            }

            string original;
            try
            {
                long fileSize = new FileInfo(path).Length;
                if (fileSize > ToolsIOHelpers.MAX_READ_BYTES)
                    return $"Error: File is too large to process ({ToolsIOHelpers.FormatBytes(fileSize)}). " +
                           "Use read_line/edit_line for targeted edits on large files.";

                original = File.ReadAllText(path, Encoding.UTF8);
            }
            catch (UnauthorizedAccessException) { return $"Error: Access denied — cannot read '{path}'."; }
            catch (IOException ex)              { return $"Error: IO error while reading '{path}': {ex.Message}"; }
            catch (Exception ex)                { return $"Error: Unexpected error while reading '{path}': {ex.Message}"; }

            int replacements = 0;
            string modified;
            try
            {
                modified = regex.Replace(original, m =>
                {
                    if (maxCount == 0 || replacements < maxCount)
                    {
                        string result = useRegex ? m.Result(newText) : newText;
                        replacements++;
                        return result;
                    }
                    return m.Value;
                });
            }
            catch (RegexMatchTimeoutException)
            {
                return "Error: Regex replacement timed out. The pattern may be too complex for this file.";
            }
            catch (FormatException ex)
            {
                return $"Error: Invalid substitution syntax in 'new_text': {ex.Message}";
            }

            if (replacements == 0)
                return $"No matches found for \"{oldText}\" — file was not modified.";

            string tempPath = path + ".tmp_" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(tempPath, modified, Encoding.UTF8);
                File.Move(tempPath, path, overwrite: true);
            }
            catch (UnauthorizedAccessException)
            {
                try { File.Delete(tempPath); } catch { }
                return $"Error: Access denied — cannot write to '{path}'.";
            }
            catch (IOException ex)
            {
                try { File.Delete(tempPath); } catch { }
                return $"Error: IO error while writing '{path}': {ex.Message}";
            }
            catch (Exception ex)
            {
                try { File.Delete(tempPath); } catch { }
                return $"Error: Unexpected error while writing '{path}': {ex.Message}";
            }

            string scope = maxCount == 0 ? "all" : $"first {maxCount}";
            return $"Replaced {replacements} occurrence(s) ({scope}) of \"{oldText}\" in '{path}'.";
        }
    }
}
