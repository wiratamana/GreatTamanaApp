using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ClosedXML.Excel;

namespace GTA.XlsxLib
{

    /// <summary>One (sheet, cell) read request.</summary>
    public sealed class CellReadRequest
    {
        public string Sheet { get; init; } = string.Empty;
        public string Cell  { get; init; } = string.Empty;
    }

    /// <summary>Result of resolving + reading a single requested cell.</summary>
    public sealed class CellReadResult
    {
        public string  Sheet             { get; set; } = string.Empty;
        public string  RequestedCell     { get; set; } = string.Empty;
        public string? ResolvedCell      { get; set; }
        public string? Value             { get; set; }
        public bool    IsMerged          { get; set; }
        public string? MergeRangeAddress { get; set; }
        public bool    IsFormula         { get; set; }
        public string? FormulaText       { get; set; }
        public bool    RecalculationStale { get; set; }
        public bool    EngineLimitation   { get; set; }
        public bool    Found             { get; set; }
        public string? Error             { get; set; }
    }

    public static partial class XlsxModifierTool
    {
        private static List<CellReadResult> ReadCellsCore(XLWorkbook workbook, IReadOnlyList<CellReadRequest> reads)
        {
            var results = new List<CellReadResult>();

            foreach (CellReadRequest req in reads)
            {
                var result = new CellReadResult { Sheet = req.Sheet ?? "", RequestedCell = req.Cell ?? "" };

                if (!TryGetSheet(workbook, result.Sheet, out IXLWorksheet? ws) || ws == null)
                {
                    result.Found = false;
                    result.Error = $"sheet \"{result.Sheet}\" not found";
                    results.Add(result);
                    continue;
                }

                if (!ResolveCell(ws, result.RequestedCell, out IXLCell? cell, out bool wasMerged,
                                  out string? mergeAddr, out string? err) || cell == null)
                {
                    result.Found = false;
                    result.Error = err ?? "could not resolve cell";
                    results.Add(result);
                    continue;
                }

                result.Found             = true;
                result.ResolvedCell      = cell.Address.ToString();
                result.IsMerged          = wasMerged;
                result.MergeRangeAddress = mergeAddr;

                try { result.RecalculationStale = cell.NeedsRecalculation; }
                catch { result.RecalculationStale = false; } // see 3.0 spike (c) in XlsxCommon.cs

                if (cell.HasFormula)
                {
                    result.IsFormula = true;
                    try { result.FormulaText = cell.FormulaA1; }
                    catch { result.FormulaText = "(unavailable)"; }

                    try { result.Value = cell.GetString(); }
                    catch (Exception exVal)
                    {
                        result.Value =
                            $"(could not be evaluated by this tool — open in Excel to see the result: {exVal.Message})";
                    }
                    result.EngineLimitation = IsEngineNameError(result.Value);
                }
                else
                {
                    try { result.Value = cell.GetString(); }
                    catch (Exception exVal) { result.Value = $"(error reading value: {exVal.Message})"; }
                }

                results.Add(result);
            }

            return results;
        }

        /// <summary>(3.10) Reads one or more (sheet, cell) pairs in a single call.</summary>
        public static string ReadCells(byte[] xlsxBytes, IReadOnlyList<CellReadRequest> reads)
        {
            var sb = new StringBuilder();
            sb.AppendLine("--- xlsx_read_cells ---");

            if (xlsxBytes == null || xlsxBytes.Length == 0 || reads == null || reads.Count == 0)
            {
                sb.AppendLine("❌ No cell reads supplied.");
                return sb.ToString().TrimEnd();
            }

            try
            {
                xlsxBytes = XlsxCompatSanitizer.EnsureLoadable(xlsxBytes);
                using var ms = new MemoryStream(xlsxBytes, writable: false);
                using var workbook = new XLWorkbook(ms);

                List<CellReadResult> results = ReadCellsCore(workbook, reads);

                foreach (CellReadResult r in results)
                {
                    if (!r.Found)
                    {
                        sb.AppendLine($"❌ {r.Sheet}!{r.RequestedCell}: {r.Error}");
                        continue;
                    }

                    string refDisplay = r.IsMerged && r.MergeRangeAddress != null
                        ? r.MergeRangeAddress.Replace(":", "~")
                        : (r.ResolvedCell ?? r.RequestedCell);

                    string valueLine = r.IsFormula
                        ? $"={r.FormulaText} → {r.Value}"
                        : (r.Value ?? "");

                    if (r.RecalculationStale)
                        valueLine += "  (cached — may be stale)";

                    if (r.EngineLimitation)
                        valueLine += $"  (⚠️ {EngineLimitationNote})";

                    sb.AppendLine($"{r.Sheet}!{refDisplay,-8}: {valueLine}");
                }

                return sb.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                sb.AppendLine($"❌ Fatal error reading workbook: {ex.Message}");
                return sb.ToString().TrimEnd();
            }
        }

        public static string ReadCellsFromBase64(string base64, IReadOnlyList<CellReadRequest> reads)
        {
            if (string.IsNullOrWhiteSpace(base64))
                throw new ArgumentException("base64 must not be null or empty.", nameof(base64));
            return ReadCells(Convert.FromBase64String(base64), reads);
        }
    }
}
