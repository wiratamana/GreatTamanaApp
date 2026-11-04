using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using PptxModifierLib;

namespace GTA.Tools
{
    internal static class ToolsPptxHelpers
    {
        internal static string? LoadLocalPptx(JsonObject input, out byte[] bytes, out string path)
        {
            bytes = Array.Empty<byte>();
            string? err = ToolsIOHelpers.ResolveExistingFile(input, "path", out path);
            if (err != null) return err;

            try
            {
                bytes = File.ReadAllBytes(path);
                return null;
            }
            catch (UnauthorizedAccessException) { return $"Error: Access denied — cannot read '{path}'."; }
            catch (IOException ex)              { return $"Error: IO error while reading '{path}': {ex.Message}"; }
            catch (Exception ex)                { return $"Error: Unexpected error while reading '{path}': {ex.Message}"; }
        }

        internal static (string? error, string savedPath) SaveLocalPptx(
            byte[] modifiedBytes, string sourcePath, string? outputPathRaw)
        {
            string targetPath = string.IsNullOrWhiteSpace(outputPathRaw)
                ? sourcePath
                : ToolsIOHelpers.CleanPath(outputPathRaw);

            try
            {
                string? dir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                string tempPath = targetPath + ".tmp_" + Guid.NewGuid().ToString("N");
                try
                {
                    File.WriteAllBytes(tempPath, modifiedBytes);
                    File.Move(tempPath, targetPath, overwrite: true);
                }
                catch
                {
                    try { File.Delete(tempPath); } catch { }
                    throw;
                }
                return (null, targetPath);
            }
            catch (UnauthorizedAccessException) { return ($"Error: Access denied — cannot write to '{targetPath}'.", targetPath); }
            catch (IOException ex)              { return ($"Error: IO error while writing '{targetPath}': {ex.Message}", targetPath); }
            catch (Exception ex)                { return ($"Error: Unexpected error while writing '{targetPath}': {ex.Message}", targetPath); }
        }

        internal static string[][] ParseTableRows(string json)
        {
            JsonNode? root = JsonNode.Parse(json);
            if (root is not JsonArray rowsNode)
                throw new FormatException(
                    "must be a JSON array of arrays of strings, e.g. [[\"Name\",\"Score\"],[\"Alice\",\"92\"]].");

            var rows = new string[rowsNode.Count][];
            for (int r = 0; r < rowsNode.Count; r++)
            {
                if (rowsNode[r] is not JsonArray rowArr)
                    throw new FormatException(
                        $"row {r} must itself be a JSON array of strings, e.g. [\"Alice\",\"92\"].");

                var row = new string[rowArr.Count];
                for (int c = 0; c < rowArr.Count; c++)
                    row[c] = rowArr[c]?.GetValue<string>() ?? "";
                rows[r] = row;
            }
            return rows;
        }

        internal static double[] ParseColumnWidths(string json)
        {
            JsonNode? root = JsonNode.Parse(json);
            if (root is not JsonArray arr)
                throw new FormatException("must be a JSON array of numbers, e.g. [0.2,0.3,0.5].");

            var result = new double[arr.Count];
            for (int i = 0; i < arr.Count; i++)
                result[i] = ToolsIOHelpers.TryGetDouble(arr[i], out double colW) ? colW : 0.0;
            return result;
        }

        internal static List<(string Text, int IndentLevel)> ParseBulletItems(string json)
        {
            JsonNode? root = JsonNode.Parse(json);
            if (root is not JsonArray arr)
                throw new FormatException(
                    "must be a JSON array of strings and/or [text, indentLevel] pairs, e.g. " +
                    "[\"Top-level point\",[\"Sub point\",1]].");

            var result = new List<(string Text, int IndentLevel)>();
            foreach (JsonNode? entry in arr)
            {
                if (entry is JsonArray pair)
                {
                    if (pair.Count < 1 || pair.Count > 2)
                        throw new FormatException("each [text, indentLevel] entry must have 1 or 2 elements.");
                    string text = pair[0]?.GetValue<string>() ?? "";
                    int level = pair.Count == 2 ? (ToolsIOHelpers.TryGetInt(pair[1], out int lvl) ? lvl : 0) : 0;
                    result.Add((text, level));
                }
                else
                {
                    result.Add((entry?.GetValue<string>() ?? "", 0));
                }
            }
            return result;
        }

        internal static string[] ParseChartCategories(string json)
        {
            JsonNode? root = JsonNode.Parse(json);
            if (root is not JsonArray arr)
                throw new FormatException("must be a JSON array of strings, e.g. [\"Cycle 1\",\"Cycle 2\"].");

            var result = new string[arr.Count];
            for (int i = 0; i < arr.Count; i++)
                result[i] = arr[i]?.GetValue<string>() ?? "";
            return result;
        }

        internal static (string Name, double[] Values)[] ParseChartSeries(string json)
        {
            JsonNode? root = JsonNode.Parse(json);
            if (root is not JsonArray arr)
                throw new FormatException(
                    "must be a JSON array of [name, values] pairs, e.g. " +
                    "[[\"Revenue\",[10,20,30]],[\"Cost\",[5,8,12]]].");

            var result = new (string Name, double[] Values)[arr.Count];
            for (int i = 0; i < arr.Count; i++)
            {
                if (arr[i] is not JsonArray pair || pair.Count != 2)
                    throw new FormatException(
                        $"series {i} must be a [name, values] 2-element array, e.g. [\"Revenue\",[10,20,30]].");

                string name = pair[0]?.GetValue<string>() ?? "";
                if (pair[1] is not JsonArray valuesArr)
                    throw new FormatException($"series {i}'s second element must be a JSON array of numbers.");

                var values = new double[valuesArr.Count];
                for (int v = 0; v < valuesArr.Count; v++)
                    values[v] = ToolsIOHelpers.TryGetDouble(valuesArr[v], out double sv) ? sv : 0.0;

                result[i] = (name, values);
            }
            return result;
        }

        internal static (double X, double Y)[] ParseCheckpoints(string json)
        {
            JsonNode? root = JsonNode.Parse(json);
            if (root is not JsonArray arr)
                throw new FormatException(
                    "must be a JSON array of [x,y] pairs, e.g. [[0.3,0.2],[0.5,0.45]].");

            var result = new (double X, double Y)[arr.Count];
            for (int i = 0; i < arr.Count; i++)
            {
                if (arr[i] is not JsonArray pair || pair.Count != 2)
                    throw new FormatException(
                        $"checkpoint {i} must be a [x,y] 2-element array, e.g. [0.3,0.2].");

                double x = ToolsIOHelpers.TryGetDouble(pair[0], out double px) ? px : 0.0;
                double y = ToolsIOHelpers.TryGetDouble(pair[1], out double py) ? py : 0.0;
                result[i] = (x, y);
            }
            return result;
        }
    }
}
