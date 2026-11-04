using System;
using System.IO;
using ClosedXML.Excel;

namespace GTA.XlsxLib
{
    public static partial class XlsxModifierTool
    {
        /// <summary>(3.13.b) Reads every non-blank cell inside one explicit range address.</summary>
        public static string ReadRange(byte[] xlsxBytes, string sheetName, string rangeAddress)
        {
            if (xlsxBytes == null || xlsxBytes.Length == 0)
                return "❌ xlsx_read_range — no workbook bytes supplied.";
            if (string.IsNullOrWhiteSpace(sheetName))
                return "❌ xlsx_read_range — 'sheet' is required. Call xlsx_query_sheets first if you don't know the sheet names.";
            if (string.IsNullOrWhiteSpace(rangeAddress))
                return "❌ xlsx_read_range — 'range' is required, e.g. \"A1:Z10\".";

            try
            {
                xlsxBytes = XlsxCompatSanitizer.EnsureLoadable(xlsxBytes);
                using var ms = new MemoryStream(xlsxBytes, writable: false);
                using var workbook = new XLWorkbook(ms);

                if (!TryGetSheet(workbook, sheetName, out IXLWorksheet? ws) || ws == null)
                    return $"❌ xlsx_read_range — sheet \"{sheetName}\" not found. Call xlsx_query_sheets to see available sheet names.";

                IXLRange range;
                try
                {
                    range = ws.Range(rangeAddress);
                }
                catch (Exception ex)
                {
                    return $"❌ xlsx_read_range — invalid range \"{rangeAddress}\": {ex.Message}";
                }

                if (range == null)
                    return $"❌ xlsx_read_range — invalid range \"{rangeAddress}\".";

                return RenderRangeReport(ws, range, $"xlsx_read_range: \"{ws.Name}\"!{rangeAddress}");
            }
            catch (Exception ex)
            {
                return $"❌ xlsx_read_range — {ex.Message}";
            }
        }

        public static string ReadRangeFromBase64(string base64, string sheetName, string rangeAddress)
        {
            if (string.IsNullOrWhiteSpace(base64))
                throw new ArgumentException("base64 must not be null or empty.", nameof(base64));
            return ReadRange(Convert.FromBase64String(base64), sheetName, rangeAddress);
        }
    }
}
