using System;
using System.Linq;
using ClosedXML.Excel;

namespace GTA.XlsxLib
{

    public sealed class XlsxOperationResult
    {
        public bool    Success       { get; init; }
        public byte[]? ModifiedBytes { get; init; }
        public string  ReportText    { get; init; } = string.Empty;
    }

    public static partial class XlsxModifierTool
    {
        // Excel sheet-name illegal characters (task 3.7 / 3.8).
        private static readonly char[] IllegalSheetNameChars = { '\\', '/', '?', '*', '[', ']', ':' };

        internal const string EngineLimitationNote =
            "this tool's built-in live-recalculation preview does not support this Excel function " +
            "yet (commonly a dynamic-array/spill function such as SORT, SORTBY, FILTER, UNIQUE, " +
            "SEQUENCE, RANDARRAY, XLOOKUP, XMATCH, LET, LAMBDA, or TEXTSPLIT). The formula text " +
            "itself WAS written/is stored correctly — this is NOT a formula error — it will compute " +
            "the correct value as soon as the workbook is opened in real Excel.";

        private static bool IsEngineNameError(string? computedValue)
            => computedValue == "#NAME?";

        private static bool TryGetSheet(XLWorkbook workbook, string name, out IXLWorksheet? ws)
        {
            ws = workbook.Worksheets.FirstOrDefault(
                s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            return ws != null;
        }

        private static bool ResolveCell(
            IXLWorksheet ws, string cellRef,
            out IXLCell? cell, out bool wasMerged,
            out string? mergeRangeAddress, out string? error)
        {
            cell = null; wasMerged = false; mergeRangeAddress = null; error = null;

            if (string.IsNullOrWhiteSpace(cellRef))
            {
                error = "cell reference is empty";
                return false;
            }

            IXLCell rawCell;
            try
            {
                rawCell = ws.Cell(cellRef);
            }
            catch (Exception ex)
            {
                error = $"invalid cell reference \"{cellRef}\": {ex.Message}";
                return false;
            }

            IXLRange? merged = null;
            try
            {
                merged = ws.MergedRanges.FirstOrDefault(
                    r => r.Cells().Any(c => c.Address.Equals(rawCell.Address)));
            }
            catch { merged = null; }

            if (merged != null)
            {
                wasMerged = true;
                mergeRangeAddress = merged.RangeAddress.ToString();
                cell = merged.FirstCell(); // anchor cell — mirrors Excel's own storage model
            }
            else
            {
                cell = rawCell;
            }
            return true;
        }

        private static bool IsValidNewSheetName(string name, out string? error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(name))
            {
                error = "sheet name must not be empty";
                return false;
            }
            if (name.Length > 31)
            {
                error = $"sheet name too long ({name.Length} chars, max 31)";
                return false;
            }
            if (name.IndexOfAny(IllegalSheetNameChars) >= 0)
            {
                error = "sheet name contains illegal character(s) (\\ / ? * [ ] :)";
                return false;
            }
            return true;
        }
    }
}
