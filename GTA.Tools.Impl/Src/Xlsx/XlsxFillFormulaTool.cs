using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ClosedXML.Excel;

namespace GTA.XlsxLib
{

    /// <summary>One "fill a formula template across a range" instruction.</summary>
    public sealed class FormulaFill
    {
        public string Sheet           { get; init; } = string.Empty;
        public string RangeAddress    { get; init; } = string.Empty;
        public string FormulaTemplate { get; init; } = string.Empty;
    }

    public static partial class XlsxModifierTool
    {
        public static XlsxOperationResult FillFormula(
            byte[] xlsxBytes, IReadOnlyList<FormulaFill> fills, bool recalculate = true)
        {
            var report = new StringBuilder();
            report.AppendLine("--- xlsx_fill_formula ---");

            int total = fills?.Count ?? 0;
            if (xlsxBytes == null || xlsxBytes.Length == 0 || fills == null || total == 0)
            {
                report.AppendLine("❌ No formula fills supplied.");
                return new XlsxOperationResult { Success = false, ModifiedBytes = null, ReportText = report.ToString().TrimEnd() };
            }

            try
            {
                xlsxBytes = XlsxCompatSanitizer.EnsureLoadable(xlsxBytes);
                using var ms = new MemoryStream(xlsxBytes, writable: false);
                using var workbook = new XLWorkbook(ms);

                int  applied           = 0;
                bool anyFormulaWritten = false;

                foreach (FormulaFill fill in fills)
                {
                    string sheetName = fill.Sheet ?? "";
                    string rangeAddr = fill.RangeAddress ?? "";
                    string template  = fill.FormulaTemplate ?? "";

                    if (!TryGetSheet(workbook, sheetName, out IXLWorksheet? ws) || ws == null)
                    {
                        report.AppendLine($"❌ {sheetName}!{rangeAddr}: sheet \"{sheetName}\" not found");
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(template))
                    {
                        report.AppendLine($"❌ {sheetName}!{rangeAddr}: formula template is empty");
                        continue;
                    }

                    IXLRange range;
                    try
                    {
                        range = ws.Range(rangeAddr);
                    }
                    catch (Exception ex)
                    {
                        report.AppendLine($"❌ {sheetName}!{rangeAddr}: invalid range: {ex.Message}");
                        continue;
                    }

                    string body = template.StartsWith("=", StringComparison.Ordinal)
                        ? (template.Length > 1 ? template.Substring(1) : "")
                        : template;

                    int cellsWritten = 0;
                    int fillErrors   = 0;

                    foreach (IXLCell cell in range.Cells())
                    {
                        string colLetter   = cell.Address.ColumnLetter;
                        int    rowNum      = cell.Address.RowNumber;
                        string formulaBody = body.Replace("{row}", rowNum.ToString())
                                                  .Replace("{col}", colLetter);
                        try
                        {
                            cell.FormulaA1 = formulaBody;
                            anyFormulaWritten = true;
                            cellsWritten++;
                        }
                        catch (Exception ex)
                        {
                            report.AppendLine($"❌ {sheetName}!{cell.Address}: {ex.Message}");
                            fillErrors++;
                        }
                    }

                    report.AppendLine(
                        $"✅ {sheetName}!{rangeAddr} — {cellsWritten} formula(s) filled from template \"{template}\"" +
                        $"{(fillErrors > 0 ? $"  ⚠️ {fillErrors} cell error(s), see above" : "")}");
                    applied++;
                }

                if (recalculate && anyFormulaWritten)
                {
                    try { workbook.RecalculateAllFormulas(); }
                    catch (Exception ex)
                    {
                        report.AppendLine(
                            $"⚠️ automatic recalculation was incomplete ({ex.Message}); " +
                            "open the file in Excel to fully recalculate.");
                    }
                }

                using var outMs = new MemoryStream();
                workbook.SaveAs(outMs);

                report.AppendLine($"{applied} of {total} formula fill(s) applied.");
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

        public static XlsxOperationResult FillFormulaFromBase64(
            string base64, IReadOnlyList<FormulaFill> fills, bool recalculate = true)
        {
            if (string.IsNullOrWhiteSpace(base64))
                throw new ArgumentException("base64 must not be null or empty.", nameof(base64));
            return FillFormula(Convert.FromBase64String(base64), fills, recalculate);
        }
    }
}
