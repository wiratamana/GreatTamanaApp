using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace GTA.Tools
{
    internal static partial class ToolsIO
    {
        [ToolTag("io-specialist")]
        [ToolInfo(
            name: "compare_files",
            description: "Compare two text files line by line and return a unified diff-style report " +
                         "showing added lines (+), removed lines (-), and unchanged context lines. " +
                         "Returns 'Files are identical' when there are no differences.")]
        [Parameter(index: 0, name: "path_a",
            type: "string",
            description: "Full absolute Windows path of the first file (the 'original').",
            required: true)]
        [Parameter(index: 1, name: "path_b",
            type: "string",
            description: "Full absolute Windows path of the second file (the 'modified').",
            required: true)]
        [Parameter(index: 2, name: "context",
            type: "integer",
            description: "Number of unchanged lines to show around each change block, " +
                         "similar to 'diff -U'. Defaults to 3.",
            required: false)]
        public static object CompareFiles(JsonObject input)
        {
            string? rawA = input["path_a"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(rawA))
                return "Error: 'path_a' parameter is missing or empty.";

            string pathA = ToolsIOHelpers.CleanPath(rawA);

            if (!File.Exists(pathA))
                return Directory.Exists(pathA)
                    ? $"Error: '{pathA}' is a directory, not a file."
                    : $"Error: File not found: '{pathA}'.";

            string? rawB = input["path_b"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(rawB))
                return "Error: 'path_b' parameter is missing or empty.";

            string pathB = ToolsIOHelpers.CleanPath(rawB);

            if (!File.Exists(pathB))
                return Directory.Exists(pathB)
                    ? $"Error: '{pathB}' is a directory, not a file."
                    : $"Error: File not found: '{pathB}'.";

            int context = 3;
            JsonNode? ctxNode = input["context"];
            if (ctxNode != null)
            {
                if (!ToolsIOHelpers.TryGetInt(ctxNode, out context))
                    return "Error: 'context' must be a valid integer.";
                if (context < 0) context = 0;
            }

            const long MAX_DIFF_BYTES = 50L * 1024 * 1024; // 50 MB per file

            long sizeA = new FileInfo(pathA).Length;
            if (sizeA > MAX_DIFF_BYTES)
                return $"Error: '{pathA}' is too large to diff ({sizeA / (1024.0 * 1024):F1} MB). " +
                       $"Maximum is {MAX_DIFF_BYTES / (1024 * 1024)} MB per file.";

            long sizeB = new FileInfo(pathB).Length;
            if (sizeB > MAX_DIFF_BYTES)
                return $"Error: '{pathB}' is too large to diff ({sizeB / (1024.0 * 1024):F1} MB). " +
                       $"Maximum is {MAX_DIFF_BYTES / (1024 * 1024)} MB per file.";

            string[] linesA, linesB;
            try { linesA = File.ReadAllLines(pathA, Encoding.UTF8); }
            catch (Exception ex) { return $"Error reading '{pathA}': {ex.Message}"; }

            try { linesB = File.ReadAllLines(pathB, Encoding.UTF8); }
            catch (Exception ex) { return $"Error reading '{pathB}': {ex.Message}"; }

            long product = (long)linesA.Length * linesB.Length;
            if (product > 4_000_000)
                return $"Error: Files are too large to diff ({linesA.Length} × {linesB.Length} lines). " +
                       $"The product of both line counts must not exceed 4 million.";

            var diff = ComputeDiff(linesA, linesB);

            bool identical = true;
            foreach (var (op, _) in diff)
                if (op != ' ') { identical = false; break; }

            if (identical)
                return $"Files are identical:\n  A: {pathA}\n  B: {pathB}";

            int aLine = 1, bLine = 1;
            var annotated = new List<(char op, string text, int aL, int bL)>(diff.Count);

            foreach (var (op, text) in diff)
            {
                annotated.Add((op, text, op != '+' ? aLine : 0, op != '-' ? bLine : 0));
                if (op != '+') aLine++;
                if (op != '-') bLine++;
            }

            var changeAt = new List<int>();
            for (int i = 0; i < annotated.Count; i++)
                if (annotated[i].op != ' ') changeAt.Add(i);

            var hunks = new List<(int start, int end)>();
            int hS = Math.Max(0, changeAt[0] - context);
            int hE = Math.Min(annotated.Count - 1, changeAt[0] + context);

            for (int k = 1; k < changeAt.Count; k++)
            {
                int cS = Math.Max(0, changeAt[k] - context);
                if (cS <= hE + 1)
                {
                    hE = Math.Min(annotated.Count - 1, changeAt[k] + context);
                }
                else
                {
                    hunks.Add((hS, hE));
                    hS = cS;
                    hE = Math.Min(annotated.Count - 1, changeAt[k] + context);
                }
            }
            hunks.Add((hS, hE));

            var sb = new StringBuilder();
            sb.AppendLine($"--- {pathA}");
            sb.AppendLine($"+++ {pathB}");

            int totalAdded = 0, totalRemoved = 0;

            foreach (var (hs, he) in hunks)
            {
                int aStart = 0, aCount = 0, bStart = 0, bCount = 0;
                bool firstA = true, firstB = true;

                for (int i = hs; i <= he; i++)
                {
                    var (op, _, al, bl) = annotated[i];
                    if (op != '+') { if (firstA) { aStart = al; firstA = false; } aCount++; }
                    if (op != '-') { if (firstB) { bStart = bl; firstB = false; } bCount++; }
                }

                sb.AppendLine($"@@ -{aStart},{aCount} +{bStart},{bCount} @@");

                for (int i = hs; i <= he; i++)
                {
                    var (op, text, _, _) = annotated[i];
                    sb.Append(op);
                    sb.Append(' ');
                    sb.AppendLine(text);

                    if (op == '+') totalAdded++;
                    if (op == '-') totalRemoved++;
                }
            }

            sb.AppendLine();
            sb.AppendLine($"Summary: {hunks.Count} hunk(s), " +
                          $"+{totalAdded} line(s) added, -{totalRemoved} line(s) removed.");

            return sb.ToString();
        }

        private static List<(char op, string text)> ComputeDiff(string[] a, string[] b)
        {
            int m = a.Length, n = b.Length;

            var dp = new int[m + 1, n + 1];
            for (int i = m - 1; i >= 0; i--)
                for (int j = n - 1; j >= 0; j--)
                    dp[i, j] = a[i] == b[j]
                        ? dp[i + 1, j + 1] + 1
                        : Math.Max(dp[i + 1, j], dp[i, j + 1]);

            var result = new List<(char, string)>(m + n);
            int ai = 0, bi = 0;

            while (ai < m || bi < n)
            {
                if (ai < m && bi < n && a[ai] == b[bi])
                {
                    result.Add((' ', a[ai])); ai++; bi++;
                }
                else if (bi >= n || (ai < m && dp[ai + 1, bi] >= dp[ai, bi + 1]))
                {
                    result.Add(('-', a[ai])); ai++;
                }
                else
                {
                    result.Add(('+', b[bi])); bi++;
                }
            }

            return result;
        }
    }
}
