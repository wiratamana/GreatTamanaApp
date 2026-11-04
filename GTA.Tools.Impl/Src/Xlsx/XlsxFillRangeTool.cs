using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ClosedXML.Excel;

namespace GTA.XlsxLib
{

    public sealed class RangeFill
    {
        public string Sheet      { get; init; } = string.Empty;
        public string AnchorCell { get; init; } = string.Empty;
        public IReadOnlyList<IReadOnlyList<string>> Grid { get; init; } = Array.Empty<IReadOnlyList<string>>();
    }

    public static partial class XlsxModifierTool
    {
        public static XlsxOperationResult FillRange(
            byte[] xlsxBytes, IReadOnlyList<RangeFill> fills, bool recalculate = true)
        {
            var report = new StringBuilder();
            report.AppendLine("--- xlsx_fill_range ---");

            int total = fills?.Count ?? 0;
            if (xlsxBytes == null || xlsxBytes.Length == 0 || fills == null || total == 0)
            {
                report.AppendLine("❌ No range fills supplied.");
                return new XlsxOperationResult { Success = false, ModifiedBytes = null, ReportText = report.ToString().TrimEnd() };
            }

            try
            {
                xlsxBytes = XlsxCompatSanitizer.EnsureLoadable(xlsxBytes);
                using var ms = new MemoryStream(xlsxBytes, writable: false);
                using var workbook = new XLWorkbook(ms);

                int  applied           = 0;
                bool anyFormulaWritten = false;

                foreach (RangeFill fill in fills)
                {
                    string sheetName = fill.Sheet ?? "";
                    string anchorRef = fill.AnchorCell ?? "";
                    IReadOnlyList<IReadOnlyList<string>> grid = fill.Grid ?? Array.Empty<IReadOnlyList<string>>();

                    if (!TryGetSheet(workbook, sheetName, out IXLWorksheet? ws) || ws == null)
                    {
                        report.AppendLine($"❌ {sheetName}!{anchorRef}: sheet \"{sheetName}\" not found");
                        continue;
                    }

                    if (grid.Count == 0)
                    {
                        report.AppendLine($"❌ {sheetName}!{anchorRef}: grid is empty — nothing to write");
                        continue;
                    }

                    IXLCell anchorCell;
                    try
                    {
                        anchorCell = ws.Cell(anchorRef);
                    }
                    catch (Exception ex)
                    {
                        report.AppendLine($"❌ {sheetName}!{anchorRef}: invalid anchor cell: {ex.Message}");
                        continue;
                    }

                    int baseRow      = anchorCell.Address.RowNumber;
                    int baseCol      = anchorCell.Address.ColumnNumber;
                    int cellsWritten = 0;
                    int maxColCount  = 0;
                    int fillErrors   = 0;

                    for (int r = 0; r < grid.Count; r++)
                    {
                        IReadOnlyList<string> rowValues = grid[r] ?? Array.Empty<string>();
                        maxColCount = Math.Max(maxColCount, rowValues.Count);

                        for (int c = 0; c < rowValues.Count; c++)
                        {
                            string raw = rowValues[c] ?? "";
                            IXLCell cell;
                            try
                            {
                                cell = ws.Cell(baseRow + r, baseCol + c);
                            }
                            catch (Exception ex)
                            {
                                report.AppendLine($"❌ {sheetName}!row {r} col {c} of grid: {ex.Message}");
                                fillErrors++;
                                continue;
                            }

                            bool isFormula = raw.StartsWith("=", StringComparison.Ordinal);
                            try
                            {
                                if (isFormula)
                                {
                                    cell.FormulaA1 = raw.Length > 1 ? raw.Substring(1) : "";
                                    anyFormulaWritten = true;
                                }
                                else
                                {
                                    cell.Value = raw; // preserved as literal text — see SetCellValues write policy
                                }
                                cellsWritten++;
                            }
                            catch (Exception ex)
                            {
                                report.AppendLine($"❌ {sheetName}!{cell.Address}: {ex.Message}");
                                fillErrors++;
                            }
                        }
                    }

                    string endCellRef = maxColCount > 0
                        ? ws.Cell(baseRow + grid.Count - 1, baseCol + maxColCount - 1).Address.ToString()
                        : anchorCell.Address.ToString();

                    report.AppendLine(
                        $"✅ {sheetName}!{anchorRef}:{endCellRef} — {cellsWritten} cell(s) written " +
                        $"({grid.Count} row(s) x {maxColCount} column(s)){(fillErrors > 0 ? $"  ⚠️ {fillErrors} cell error(s), see above" : "")}");
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

                report.AppendLine($"{applied} of {total} range fill(s) applied.");
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

        public static XlsxOperationResult FillRangeFromBase64(
            string base64, IReadOnlyList<RangeFill> fills, bool recalculate = true)
        {
            if (string.IsNullOrWhiteSpace(base64))
                throw new ArgumentException("base64 must not be null or empty.", nameof(base64));
            return FillRange(Convert.FromBase64String(base64), fills, recalculate);
        }
    }
}
