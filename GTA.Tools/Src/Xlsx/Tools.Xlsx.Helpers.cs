using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using GTA.XlsxLib;

namespace GTA.Tools
{
    internal static class ToolsXlsxHelpers
    {
        internal static string? LoadLocalXlsx(JsonObject input, out byte[] bytes, out string path)
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

        internal static (string? error, string savedPath) SaveLocalXlsx(
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

        internal static List<CellWrite> ParseCellWrites(string json)
        {
            var result = new List<CellWrite>();
            JsonNode? root = JsonNode.Parse(json);
            if (root is not JsonArray groups)
                throw new FormatException(
                    "must be a JSON array of arrays, e.g. [[\"Sheet A\",\"A2\",\"Hello\",\"B2\",\"World\"]].");

            foreach (JsonNode? groupNode in groups)
            {
                if (groupNode is not JsonArray group || group.Count < 3 || group.Count % 2 == 0)
                    throw new FormatException(
                        "each group must be [sheet, cell, value, cell, value, ...] " +
                        "(sheet name followed by one or more complete (cell, value) pairs).");

                string sheet = group[0]?.GetValue<string>() ?? "";
                for (int i = 1; i + 1 < group.Count; i += 2)
                {
                    string cell  = group[i]?.GetValue<string>()     ?? "";
                    string value = group[i + 1]?.GetValue<string>() ?? "";
                    result.Add(new CellWrite { Sheet = sheet, Cell = cell, Value = value });
                }
            }
            return result;
        }

        internal static List<CellReadRequest> ParseCellReads(string json)
        {
            var result = new List<CellReadRequest>();
            JsonNode? root = JsonNode.Parse(json);
            if (root is not JsonArray groups)
                throw new FormatException(
                    "must be a JSON array of arrays, e.g. [[\"Sheet A\",\"A2\",\"B2\"]].");

            foreach (JsonNode? groupNode in groups)
            {
                if (groupNode is not JsonArray group || group.Count < 2)
                    throw new FormatException(
                        "each group must be [sheet, cell, cell, ...] (sheet name followed by one or more cell refs).");

                string sheet = group[0]?.GetValue<string>() ?? "";
                for (int i = 1; i < group.Count; i++)
                {
                    string cell = group[i]?.GetValue<string>() ?? "";
                    result.Add(new CellReadRequest { Sheet = sheet, Cell = cell });
                }
            }
            return result;
        }

        /// <summary>Parses the "sheet_names" shape: ["New Sheet 1","New Sheet 2"]</summary>
        internal static List<string> ParseSheetNames(string json)
        {
            JsonNode? root = JsonNode.Parse(json);
            if (root is not JsonArray arr)
                throw new FormatException("must be a JSON array of strings, e.g. [\"New Sheet 1\",\"New Sheet 2\"].");

            var result = new List<string>();
            foreach (JsonNode? n in arr)
                result.Add(n?.GetValue<string>() ?? "");
            return result;
        }

        /// <summary>Parses the "sheet_renames" shape: [["OldName1","NewName1"],["OldName2","NewName2"]]</summary>
        internal static List<SheetRename> ParseSheetRenames(string json)
        {
            JsonNode? root = JsonNode.Parse(json);
            if (root is not JsonArray groups)
                throw new FormatException(
                    "must be a JSON array of [oldName, newName] pairs, e.g. [[\"Old\",\"New\"]].");

            var result = new List<SheetRename>();
            foreach (JsonNode? groupNode in groups)
            {
                if (groupNode is not JsonArray group || group.Count != 2)
                    throw new FormatException("each entry must be exactly [oldName, newName].");

                result.Add(new SheetRename
                {
                    OldName = group[0]?.GetValue<string>() ?? "",
                    NewName = group[1]?.GetValue<string>() ?? ""
                });
            }
            return result;
        }

        internal static List<MergeOp> ParseMergeOps(string json, bool merge)
        {
            JsonNode? root = JsonNode.Parse(json);
            if (root is not JsonArray groups)
                throw new FormatException(
                    "must be a JSON array of [sheet, rangeAddress] pairs, e.g. [[\"Sheet A\",\"A1:C3\"]].");

            var result = new List<MergeOp>();
            foreach (JsonNode? groupNode in groups)
            {
                if (groupNode is not JsonArray group || group.Count != 2)
                    throw new FormatException("each entry must be exactly [sheet, rangeAddress].");

                result.Add(new MergeOp
                {
                    Sheet        = group[0]?.GetValue<string>() ?? "",
                    RangeAddress = group[1]?.GetValue<string>() ?? "",
                    Merge        = merge
                });
            }
            return result;
        }

        /// <summary>Parses the "duplicates" shape: [["SourceSheet1","NewSheet1"],["SourceSheet2","NewSheet2"]]</summary>
        internal static List<SheetDuplicate> ParseSheetDuplicates(string json)
        {
            JsonNode? root = JsonNode.Parse(json);
            if (root is not JsonArray groups)
                throw new FormatException(
                    "must be a JSON array of [sourceSheetName, newSheetName] pairs, e.g. [[\"Template\",\"Copy1\"]].");

            var result = new List<SheetDuplicate>();
            foreach (JsonNode? groupNode in groups)
            {
                if (groupNode is not JsonArray group || group.Count != 2)
                    throw new FormatException("each entry must be exactly [sourceSheetName, newSheetName].");

                result.Add(new SheetDuplicate
                {
                    SourceName = group[0]?.GetValue<string>() ?? "",
                    NewName    = group[1]?.GetValue<string>() ?? ""
                });
            }
            return result;
        }

        internal static List<RangeFill> ParseRangeFills(string json)
        {
            JsonNode? root = JsonNode.Parse(json);
            if (root is not JsonArray groups)
                throw new FormatException(
                    "must be a JSON array of [sheet, anchorCell, grid] entries, e.g. " +
                    "[[\"Sheet A\",\"B2\",[[\"h1\",\"h2\"],[\"v1\",\"v2\"]]]].");

            var result = new List<RangeFill>();
            foreach (JsonNode? groupNode in groups)
            {
                if (groupNode is not JsonArray group || group.Count != 3)
                    throw new FormatException(
                        "each entry must be exactly [sheet, anchorCell, grid] where grid is a JSON " +
                        "array of arrays of strings, e.g. [\"Sheet A\",\"B2\",[[\"h1\",\"h2\"],[\"v1\",\"v2\"]]].");

                string sheet  = group[0]?.GetValue<string>() ?? "";
                string anchor = group[1]?.GetValue<string>() ?? "";

                if (group[2] is not JsonArray gridNode)
                    throw new FormatException(
                        "the third entry (grid) must be a JSON array of arrays of strings, " +
                        "e.g. [[\"h1\",\"h2\"],[\"v1\",\"v2\"]].");

                var grid = new List<IReadOnlyList<string>>();
                foreach (JsonNode? rowNode in gridNode)
                {
                    if (rowNode is not JsonArray rowArr)
                        throw new FormatException(
                            "every row in the grid must itself be a JSON array of strings, " +
                            "e.g. [\"v1\",\"v2\"].");

                    var row = new List<string>();
                    foreach (JsonNode? cellNode in rowArr)
                        row.Add(cellNode?.GetValue<string>() ?? "");
                    grid.Add(row);
                }

                result.Add(new RangeFill { Sheet = sheet, AnchorCell = anchor, Grid = grid });
            }
            return result;
        }

        internal static List<FormulaFill> ParseFormulaFills(string json)
        {
            JsonNode? root = JsonNode.Parse(json);
            if (root is not JsonArray groups)
                throw new FormatException(
                    "must be a JSON array of [sheet, rangeAddress, formulaTemplate] entries, e.g. " +
                    "[[\"Sheet A\",\"Z8:Z115\",\"=CONCAT(C{row},\\\"_\\\",D{row})\"]].");

            var result = new List<FormulaFill>();
            foreach (JsonNode? groupNode in groups)
            {
                if (groupNode is not JsonArray group || group.Count != 3)
                    throw new FormatException("each entry must be exactly [sheet, rangeAddress, formulaTemplate].");

                result.Add(new FormulaFill
                {
                    Sheet           = group[0]?.GetValue<string>() ?? "",
                    RangeAddress    = group[1]?.GetValue<string>() ?? "",
                    FormulaTemplate = group[2]?.GetValue<string>() ?? ""
                });
            }
            return result;
        }
    }
}
