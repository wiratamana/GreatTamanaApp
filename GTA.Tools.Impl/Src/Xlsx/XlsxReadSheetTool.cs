using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ClosedXML.Excel;

namespace GTA.XlsxLib
{
    public static partial class XlsxModifierTool
    {

        public static string ReadSheet(byte[] xlsxBytes, string sheetName)
        {
            if (xlsxBytes == null || xlsxBytes.Length == 0)
                return "❌ xlsx_read_sheet — no workbook bytes supplied.";
            if (string.IsNullOrWhiteSpace(sheetName))
                return "❌ xlsx_read_sheet — 'sheet' is required. Call xlsx_query_sheets first if you don't know the sheet names.";

            try
            {
                xlsxBytes = XlsxCompatSanitizer.EnsureLoadable(xlsxBytes);
                using var ms = new MemoryStream(xlsxBytes, writable: false);
                using var workbook = new XLWorkbook(ms);

                if (!TryGetSheet(workbook, sheetName, out IXLWorksheet? ws) || ws == null)
                    return $"❌ xlsx_read_sheet — sheet \"{sheetName}\" not found. Call xlsx_query_sheets to see available sheet names.";

                IXLRange? used;
                try { used = ws.RangeUsed(); }
                catch (Exception ex) { return $"❌ xlsx_read_sheet — could not determine used range: {ex.Message}"; }

                if (used == null)
                    return $"--- xlsx_read_sheet: \"{ws.Name}\" ---\n(sheet is empty — no cells contain data)";

                return RenderRangeReport(ws, used, $"xlsx_read_sheet: \"{ws.Name}\"");
            }
            catch (Exception ex)
            {
                return $"❌ xlsx_read_sheet — {ex.Message}";
            }
        }

        public static string ReadSheetFromBase64(string base64, string sheetName)
        {
            if (string.IsNullOrWhiteSpace(base64))
                throw new ArgumentException("base64 must not be null or empty.", nameof(base64));
            return ReadSheet(Convert.FromBase64String(base64), sheetName);
        }

        /// <summary>Shared renderer for ReadSheet/ReadRange (3.13).</summary>
        private static string RenderRangeReport(IXLWorksheet ws, IXLRange range, string header)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"--- {header} ---");
            sb.AppendLine($"Range: {range.RangeAddress} ({range.RowCount()} row(s) x {range.ColumnCount()} column(s))");
            sb.AppendLine();

            int firstRow = range.RangeAddress.FirstAddress.RowNumber;
            int lastRow  = range.RangeAddress.LastAddress.RowNumber;
            int firstCol = range.RangeAddress.FirstAddress.ColumnNumber;
            int lastCol  = range.RangeAddress.LastAddress.ColumnNumber;

            List<IXLRange> mergesInRange;
            try
            {
                mergesInRange = ws.MergedRanges
                    .Where(m => m.RangeAddress.FirstAddress.RowNumber    <= lastRow &&
                                m.RangeAddress.LastAddress.RowNumber     >= firstRow &&
                                m.RangeAddress.FirstAddress.ColumnNumber <= lastCol &&
                                m.RangeAddress.LastAddress.ColumnNumber  >= firstCol)
                    .ToList();
            }
            catch { mergesInRange = new List<IXLRange>(); }

            var mergeAnchorAddr = new Dictionary<string, string>(); // "row,col" (anchor only) -> "A1:C1"
            var mergeMemberSet  = new HashSet<string>();            // "row,col" for EVERY cell the merge covers

            foreach (IXLRange m in mergesInRange)
            {
                try
                {
                    IXLCell anchor = m.FirstCell();
                    string  key    = $"{anchor.Address.RowNumber},{anchor.Address.ColumnNumber}";
                    mergeAnchorAddr[key] = m.RangeAddress.ToString();
                    foreach (IXLCell c in m.Cells())
                        mergeMemberSet.Add($"{c.Address.RowNumber},{c.Address.ColumnNumber}");
                }
                catch { }
            }

            var formulaLines  = new List<string>();
            bool anyDataFound = false;

            for (int r = firstRow; r <= lastRow; r++)
            {
                var cellsThisRow = new List<string>();

                for (int c = firstCol; c <= lastCol; c++)
                {
                    string key          = $"{r},{c}";
                    bool   isMergeMember = mergeMemberSet.Contains(key);
                    bool   isAnchor      = mergeAnchorAddr.ContainsKey(key);

                    // Non-anchor cell of a merge: value lives on the anchor only — skip.
                    if (isMergeMember && !isAnchor) continue;

                    IXLCell cell;
                    try { cell = ws.Cell(r, c); }
                    catch { continue; }

                    string cellRef = cell.Address.ToString();

                    string value;
                    try { value = cell.GetString(); }
                    catch (Exception ex) { value = $"(error reading value: {ex.Message})"; }

                    if (string.IsNullOrEmpty(value)) continue; // blank cell — skip for compactness

                    bool   isEngineNameError = cell.HasFormula && IsEngineNameError(value);
                    string note = isAnchor ? $"  (merged {mergeAnchorAddr[key]})" : "";
                    if (isEngineNameError) note += "  (⚠️ see Formulas note below)";
                    cellsThisRow.Add($"  {cellRef}: {value}{note}");
                    anyDataFound = true;

                    if (cell.HasFormula)
                    {
                        string formulaText;
                        try { formulaText = cell.FormulaA1; } catch { formulaText = "(unavailable)"; }
                        string formulaNote = isEngineNameError ? $"  (⚠️ {EngineLimitationNote})" : "";
                        formulaLines.Add($"  {cellRef}: ={formulaText} → {value}{formulaNote}");
                    }
                }

                if (cellsThisRow.Count > 0)
                {
                    sb.AppendLine($"Row {r}:");
                    foreach (string line in cellsThisRow) sb.AppendLine(line);
                }
            }

            if (!anyDataFound)
                sb.AppendLine("(no non-blank cells found in this range)");

            if (mergesInRange.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Merged cells in range:");
                foreach (string m in mergesInRange.Select(m => m.RangeAddress.ToString()).Distinct())
                    sb.AppendLine($"  {m}");
            }

            if (formulaLines.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Formulas:");
                foreach (string fl in formulaLines) sb.AppendLine(fl);
            }

            return sb.ToString().TrimEnd();
        }
    }
}
