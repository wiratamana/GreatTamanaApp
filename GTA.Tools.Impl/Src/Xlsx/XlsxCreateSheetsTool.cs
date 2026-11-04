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
        /// <summary>(3.7) Creates one or more new worksheets by name in a single call.</summary>
        public static XlsxOperationResult CreateSheets(byte[] xlsxBytes, IReadOnlyList<string> sheetNames)
        {
            var report = new StringBuilder();
            report.AppendLine("--- xlsx_create_sheets ---");

            int total = sheetNames?.Count ?? 0;
            if (xlsxBytes == null || xlsxBytes.Length == 0 || sheetNames == null || total == 0)
            {
                report.AppendLine("❌ No sheet names supplied.");
                return new XlsxOperationResult { Success = false, ModifiedBytes = null, ReportText = report.ToString().TrimEnd() };
            }

            try
            {
                xlsxBytes = XlsxCompatSanitizer.EnsureLoadable(xlsxBytes);
                using var ms = new MemoryStream(xlsxBytes, writable: false);
                using var workbook = new XLWorkbook(ms);

                int applied = 0;
                var createdThisBatch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (string rawName in sheetNames)
                {
                    string name = rawName ?? string.Empty;

                    if (!IsValidNewSheetName(name, out string? nameErr))
                    {
                        report.AppendLine($"❌ \"{name}\": {nameErr}");
                        continue;
                    }

                    bool existsAlready = workbook.Worksheets.Any(
                        s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
                    bool dupInBatch = createdThisBatch.Contains(name);

                    if (existsAlready || dupInBatch)
                    {
                        string reason = existsAlready
                            ? "a sheet with this name already exists"
                            : "duplicate name within this same request";
                        report.AppendLine($"❌ \"{name}\": {reason}");
                        continue;
                    }

                    try
                    {
                        workbook.Worksheets.Add(name);
                        createdThisBatch.Add(name);
                        report.AppendLine($"✅ \"{name}\" created");
                        applied++;
                    }
                    catch (Exception ex)
                    {
                        report.AppendLine($"❌ \"{name}\": {ex.Message}");
                    }
                }

                using var outMs = new MemoryStream();
                workbook.SaveAs(outMs);

                report.AppendLine($"{applied} of {total} sheet(s) created.");
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

        public static XlsxOperationResult CreateSheetsFromBase64(string base64, IReadOnlyList<string> sheetNames)
        {
            if (string.IsNullOrWhiteSpace(base64))
                throw new ArgumentException("base64 must not be null or empty.", nameof(base64));
            return CreateSheets(Convert.FromBase64String(base64), sheetNames);
        }
    }
}
