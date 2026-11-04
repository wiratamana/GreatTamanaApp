using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ClosedXML.Excel;

namespace GTA.XlsxLib
{

    public sealed class SheetDuplicate
    {
        public string SourceName { get; init; } = string.Empty;
        public string NewName    { get; init; } = string.Empty;
    }

    public static partial class XlsxModifierTool
    {
        public static XlsxOperationResult DuplicateSheets(byte[] xlsxBytes, IReadOnlyList<SheetDuplicate> ops)
        {
            var report = new StringBuilder();
            report.AppendLine("--- xlsx_duplicate_sheets ---");

            int total = ops?.Count ?? 0;
            if (xlsxBytes == null || xlsxBytes.Length == 0 || ops == null || total == 0)
            {
                report.AppendLine("❌ No sheet duplication operations supplied.");
                return new XlsxOperationResult { Success = false, ModifiedBytes = null, ReportText = report.ToString().TrimEnd() };
            }

            try
            {
                xlsxBytes = XlsxCompatSanitizer.EnsureLoadable(xlsxBytes);
                using var ms = new MemoryStream(xlsxBytes, writable: false);
                using var workbook = new XLWorkbook(ms);

                int applied = 0;
                var createdThisBatch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (SheetDuplicate op in ops)
                {
                    string sourceName = op.SourceName ?? "";
                    string newName    = op.NewName ?? "";

                    if (!TryGetSheet(workbook, sourceName, out IXLWorksheet? source) || source == null)
                    {
                        report.AppendLine($"❌ \"{sourceName}\" → \"{newName}\": source sheet \"{sourceName}\" not found");
                        continue;
                    }

                    if (!IsValidNewSheetName(newName, out string? nameErr))
                    {
                        report.AppendLine($"❌ \"{sourceName}\" → \"{newName}\": {nameErr}");
                        continue;
                    }

                    bool existsAlready = workbook.Worksheets.Any(
                        s => string.Equals(s.Name, newName, StringComparison.OrdinalIgnoreCase));
                    bool dupInBatch = createdThisBatch.Contains(newName);

                    if (existsAlready || dupInBatch)
                    {
                        string reason = existsAlready
                            ? "a sheet with this name already exists"
                            : "duplicate name within this same request";
                        report.AppendLine($"❌ \"{sourceName}\" → \"{newName}\": {reason}");
                        continue;
                    }

                    try
                    {
                        source.CopyTo(newName);
                        createdThisBatch.Add(newName);
                        report.AppendLine($"✅ \"{sourceName}\" → \"{newName}\" (cells, formulas, formatting, merges copied)");
                        applied++;
                    }
                    catch (Exception ex)
                    {
                        report.AppendLine($"❌ \"{sourceName}\" → \"{newName}\": {ex.Message}");
                    }
                }

                using var outMs = new MemoryStream();
                workbook.SaveAs(outMs);

                report.AppendLine($"{applied} of {total} sheet(s) duplicated.");
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

        public static XlsxOperationResult DuplicateSheetsFromBase64(string base64, IReadOnlyList<SheetDuplicate> ops)
        {
            if (string.IsNullOrWhiteSpace(base64))
                throw new ArgumentException("base64 must not be null or empty.", nameof(base64));
            return DuplicateSheets(Convert.FromBase64String(base64), ops);
        }
    }
}
