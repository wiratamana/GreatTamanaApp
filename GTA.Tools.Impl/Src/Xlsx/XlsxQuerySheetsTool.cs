using System;
using System.IO;
using System.Linq;
using System.Text;
using ClosedXML.Excel;

namespace GTA.XlsxLib
{

    /// <summary>One worksheet's discovery summary — see <see cref="XlsxModifierTool.QuerySheets"/>.</summary>
    public sealed class SheetInfo
    {
        public string  Name             { get; init; } = string.Empty;
        public string? UsedRangeAddress { get; init; }
    }

    public static partial class XlsxModifierTool
    {
        /// <summary>(3.11) Lists every worksheet in the workbook — no parameters.</summary>
        public static string QuerySheets(byte[] xlsxBytes)
        {
            if (xlsxBytes == null || xlsxBytes.Length == 0)
                return "❌ xlsx_query_sheets — no workbook bytes supplied.";

            try
            {
                xlsxBytes = XlsxCompatSanitizer.EnsureLoadable(xlsxBytes);
                using var ms = new MemoryStream(xlsxBytes, writable: false);
                using var workbook = new XLWorkbook(ms);

                var sheets = workbook.Worksheets.ToList();
                var sb = new StringBuilder();
                sb.AppendLine($"--- Workbook sheets ({sheets.Count}) ---");

                if (sheets.Count == 0)
                {
                    sb.AppendLine("(workbook has zero sheets)");
                    return sb.ToString().TrimEnd();
                }

                for (int i = 0; i < sheets.Count; i++)
                {
                    IXLWorksheet ws = sheets[i];
                    string used;
                    try
                    {
                        IXLRange? usedRange = ws.RangeUsed();
                        used = usedRange != null ? $"used range: {usedRange.RangeAddress}" : "(empty)";
                    }
                    catch { used = "(empty)"; }

                    sb.AppendLine($"{i + 1}. \"{ws.Name}\"    {used}");
                }

                return sb.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                return $"❌ xlsx_query_sheets — could not read workbook: {ex.Message}";
            }
        }

        public static string QuerySheetsFromBase64(string base64)
        {
            if (string.IsNullOrWhiteSpace(base64))
                throw new ArgumentException("base64 must not be null or empty.", nameof(base64));
            return QuerySheets(Convert.FromBase64String(base64));
        }
    }
}
