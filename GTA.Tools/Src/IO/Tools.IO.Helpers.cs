using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using GTA.Tools.Impl.IO;

namespace GTA.Tools
{
    public static class ToolsIOHelpers
    {
        /// <summary>Maximum file size allowed for full in-memory reads (50 MB).</summary>
        internal const long MAX_READ_BYTES = FileIoUtils.MAX_READ_BYTES;

        public static string GetMediaType(string filePath) => FileIoUtils.GetMediaType(filePath);

        /// <summary>Strip surrounding double-quotes and whitespace from a raw path string.</summary>
        internal static string CleanPath(string raw) => FileIoUtils.CleanPath(raw);

        internal static string? ResolvePath(JsonObject input, out string path)
            => ResolveExistingFile(input, "path", out path);

        internal static string? ResolveExistingFile(JsonObject input, string key, out string path)
        {
            path = string.Empty;
            string? raw = input[key]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(raw))
                return $"Error: '{key}' parameter is missing or empty.";
            path = CleanPath(raw);
            if (System.IO.Directory.Exists(path))
                return $"Error: '{path}' is a directory, not a file.";
            if (!System.IO.File.Exists(path))
                return $"Error: File not found at '{path}'.";
            return null;
        }

        internal static string? ResolveDir(JsonObject input, out string path)
            => ResolveExistingDir(input, "path", out path);

        internal static string? ResolveExistingDir(JsonObject input, string key, out string path)
        {
            path = string.Empty;
            string? raw = input[key]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(raw))
                return $"Error: '{key}' parameter is missing or empty.";
            path = CleanPath(raw);
            if (System.IO.File.Exists(path))
                return $"Error: '{path}' is a file, not a directory.";
            if (!System.IO.Directory.Exists(path))
                return $"Error: Directory not found at '{path}'.";
            return null;
        }

        internal static string? ResolveNewPath(JsonObject input, string key, out string path)
        {
            path = string.Empty;
            string? raw = input[key]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(raw))
                return $"Error: '{key}' parameter is missing or empty.";
            path = CleanPath(raw);
            return null;
        }

        internal static bool GetOptionalBool(JsonObject input, string key, bool defaultValue = false)
        {
            JsonNode? node = input[key];
            if (node is null) return defaultValue;

            if (node is JsonValue value)
            {
                if (value.TryGetValue(out bool b)) return b;

                if (value.TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s))
                {
                    s = s.Trim();
                    if (bool.TryParse(s, out bool parsed)) return parsed;
                    if (s == "1") return true;
                    if (s == "0") return false;
                    return defaultValue;
                }

                if (value.TryGetValue(out double d)) return d != 0;
            }

            return defaultValue;
        }

        internal static bool TryGetInt(JsonNode? node, out int value)
        {
            value = 0;
            if (node is null) return false;

            if (node is JsonValue jv)
            {
                // Fast path: already a native JSON number backed by an int.
                if (jv.TryGetValue(out int i)) { value = i; return true; }

                // Tolerant path: value arrived as a quoted string, e.g. "200".
                if (jv.TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s) &&
                    int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                {
                    value = parsed;
                    return true;
                }

                // Tolerant path: whole-number floating point (e.g. 200.0).
                if (jv.TryGetValue(out double d) && d == Math.Floor(d) &&
                    d >= int.MinValue && d <= int.MaxValue)
                {
                    value = (int)d;
                    return true;
                }
            }

            return false;
        }

        internal static bool TryGetDouble(JsonNode? node, out double value)
        {
            value = 0;
            if (node is null) return false;

            if (node is JsonValue jv)
            {
                // Fast path: already a native JSON number.
                if (jv.TryGetValue(out double d)) { value = d; return true; }

                // Tolerant path: value arrived as a quoted string, e.g. "0.1".
                if (jv.TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s) &&
                    double.TryParse(s.Trim(), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double parsed))
                {
                    value = parsed;
                    return true;
                }
            }

            return false;
        }

        internal static int? GetOptionalInt(JsonNode? node) => TryGetInt(node, out int v) ? v : (int?)null;

        internal static double? GetOptionalDouble(JsonNode? node) => TryGetDouble(node, out double v) ? v : (double?)null;

        internal static string? ReadLines(string path, out List<string> lines, out bool hadTrailingNewline)
            => FileIoUtils.ReadLines(path, out lines, out hadTrailingNewline);

        internal static string? WriteLines(string path, List<string> lines, bool hadTrailingNewline = true, bool addBom = false)
            => FileIoUtils.WriteLines(path, lines, hadTrailingNewline, addBom);

        /// <summary>Convert a byte count to a human-readable string (e.g. "4.2 KB", "1.3 MB").</summary>
        internal static string FormatBytes(long bytes) => FileIoUtils.FormatBytes(bytes);

        internal static bool IsBinaryExtension(string path) => FileIoUtils.IsBinaryExtension(path);

        internal static bool IsBinaryContent(string path, int sampleSize = 8000)
            => FileIoUtils.IsBinaryContent(path, sampleSize);
    }
}
