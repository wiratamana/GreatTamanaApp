using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ClosedXML.Excel;

namespace GTA.XlsxLib
{

    /// <summary>One (sheet, cell, value) write instruction.</summary>
    public sealed class CellWrite
    {
        public string Sheet { get; init; } = string.Empty;
        public string Cell  { get; init; } = string.Empty;
        public string Value { get; init; } = string.Empty;
    }

    public static partial class XlsxModifierTool
    {

        /// <summary>(3.6) Writes one or more cell values/formulas into the workbook in a single pass.</summary>
        public static XlsxOperationResult SetCellValues(
            byte[] xlsxBytes, IReadOnlyList<CellWrite> writes, bool recalculate = true)
        {
            var report = new StringBuilder();
            report.AppendLine("--- xlsx_set_cell_values ---");

            int total = writes?.Count ?? 0;
            if (xlsxBytes == null || xlsxBytes.Length == 0 || writes == null || total == 0)
            {
                report.AppendLine("❌ No cell writes supplied.");
                return new XlsxOperationResult { Success = false, ModifiedBytes = null, ReportText = report.ToString().TrimEnd() };
            }

            try
            {
                xlsxBytes = XlsxCompatSanitizer.EnsureLoadable(xlsxBytes);
                using var ms = new MemoryStream(xlsxBytes, writable: false);
                using var workbook = new XLWorkbook(ms);

                int  applied           = 0;
                bool anyFormulaWritten = false;

                foreach (CellWrite w in writes)
                {
                    string sheetName = w.Sheet ?? "";
                    string cellRef   = w.Cell  ?? "";
                    string value     = w.Value ?? "";

                    if (!TryGetSheet(workbook, sheetName, out IXLWorksheet? ws) || ws == null)
                    {
                        report.AppendLine($"❌ {sheetName}!{cellRef}: sheet \"{sheetName}\" not found");
                        continue;
                    }

                    if (!ResolveCell(ws, cellRef, out IXLCell? cell, out bool wasMerged,
                                      out string? mergeAddr, out string? resolveErr) || cell == null)
                    {
                        report.AppendLine($"❌ {sheetName}!{cellRef}: {resolveErr}");
                        continue;
                    }

                    string refDisplay = wasMerged && mergeAddr != null
                        ? mergeAddr.Replace(":", "~")
                        : cellRef;
                    string mergeNote = wasMerged
                        ? $"  (merged range — wrote to anchor {cell.Address})"
                        : "";

                    bool isFormula = value.StartsWith("=", StringComparison.Ordinal);
                    if (isFormula)
                    {
                        string formulaBody = value.Length > 1 ? value.Substring(1) : "";
                        try
                        {
                            cell.FormulaA1 = formulaBody;
                            anyFormulaWritten = true;

                            // Per 3.0 spike (a): ClosedXML evaluates on access
                            // immediately, no explicit recalc needed for this.
                            string computed;
                            try { computed = cell.GetString(); }
                            catch (Exception exVal)
                            {
                                computed = $"(not yet calculated — open in Excel to compute: {exVal.Message})";
                            }

                            string statusNote = IsEngineNameError(computed)
                                ? $"  (⚠️ {EngineLimitationNote})"
                                : "  (formula, recalculated)";

                            report.AppendLine(
                                $"✅ {sheetName}!{refDisplay,-8}: ={formulaBody} → {computed}{statusNote}{mergeNote}");
                            applied++;
                        }
                        catch (Exception ex)
                        {
                            report.AppendLine($"❌ {sheetName}!{refDisplay}: invalid formula: {ex.Message}");
                        }
                    }
                    else
                    {
                        try
                        {
                            cell.Value = value; // preserved as literal text — see write policy above
                            report.AppendLine($"✅ {sheetName}!{refDisplay,-8}: {value}{mergeNote}");
                            applied++;
                        }
                        catch (Exception ex)
                        {
                            report.AppendLine($"❌ {sheetName}!{refDisplay}: {ex.Message}");
                        }
                    }
                }

                if (recalculate && anyFormulaWritten)
                {
                    try
                    {
                        workbook.RecalculateAllFormulas();
                    }
                    catch (Exception ex)
                    {
                        report.AppendLine(
                            $"⚠️ automatic recalculation was incomplete ({ex.Message}); " +
                            "open the file in Excel to fully recalculate.");
                    }
                }

                using var outMs = new MemoryStream();
                workbook.SaveAs(outMs);

                report.AppendLine($"{applied} of {total} write(s) applied.");
                return new XlsxOperationResult
                {
                    Success       = true,
                    ModifiedBytes = outMs.ToArray(),
                    ReportText    = report.ToString().TrimEnd()
                };
            }
            catch (Exception ex)
            {
                report.AppendLine($"❌ Fatal error: {ex.Message}");
                return new XlsxOperationResult { Success = false, ModifiedBytes = null, ReportText = report.ToString().TrimEnd() };
            }
        }

        public static XlsxOperationResult SetCellValuesFromBase64(
            string base64, IReadOnlyList<CellWrite> writes, bool recalculate = true)
        {
            if (string.IsNullOrWhiteSpace(base64))
                throw new ArgumentException("base64 must not be null or empty.", nameof(base64));
            return SetCellValues(Convert.FromBase64String(base64), writes, recalculate);
        }
    }
}
