using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ClosedXML.Excel;

namespace GTA.XlsxLib
{

    /// <summary>One worksheet rename instruction.</summary>
    public sealed class SheetRename
    {
        public string OldName { get; init; } = string.Empty;
        public string NewName { get; init; } = string.Empty;
    }

    public static partial class XlsxModifierTool
    {
        /// <summary>(3.8) Renames one or more existing worksheets in a single call.</summary>
        public static XlsxOperationResult RenameSheets(byte[] xlsxBytes, IReadOnlyList<SheetRename> renames)
        {
            var report = new StringBuilder();
            report.AppendLine("--- xlsx_rename_sheets ---");

            int total = renames?.Count ?? 0;
            if (xlsxBytes == null || xlsxBytes.Length == 0 || renames == null || total == 0)
            {
                report.AppendLine("❌ No sheet renames supplied.");
                return new XlsxOperationResult { Success = false, ModifiedBytes = null, ReportText = report.ToString().TrimEnd() };
            }

            try
            {
                xlsxBytes = XlsxCompatSanitizer.EnsureLoadable(xlsxBytes);
                using var ms = new MemoryStream(xlsxBytes, writable: false);
                using var workbook = new XLWorkbook(ms);

                int applied = 0;

                foreach (SheetRename rn in renames)
                {
                    string oldName = rn.OldName ?? "";
                    string newName = rn.NewName ?? "";

                    if (!TryGetSheet(workbook, oldName, out IXLWorksheet? ws) || ws == null)
                    {
                        report.AppendLine($"❌ \"{oldName}\" → \"{newName}\": sheet \"{oldName}\" not found");
                        continue;
                    }

                    if (!IsValidNewSheetName(newName, out string? nameErr))
                    {
                        report.AppendLine($"❌ \"{oldName}\" → \"{newName}\": {nameErr}");
                        continue;
                    }

                    bool collides = workbook.Worksheets.Any(
                        s => !ReferenceEquals(s, ws) &&
                             string.Equals(s.Name, newName, StringComparison.OrdinalIgnoreCase));
                    if (collides)
                    {
                        report.AppendLine(
                            $"❌ \"{oldName}\" → \"{newName}\": a different sheet already named \"{newName}\" exists");
                        continue;
                    }

                    try
                    {
                        ws.Name = newName;
                        report.AppendLine($"✅ \"{oldName}\" → \"{newName}\"");
                        applied++;
                    }
                    catch (Exception ex)
                    {
                        report.AppendLine($"❌ \"{oldName}\" → \"{newName}\": {ex.Message}");
                    }
                }

                using var outMs = new MemoryStream();
                workbook.SaveAs(outMs);

                report.AppendLine($"{applied} of {total} rename(s) applied.");
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

        public static XlsxOperationResult RenameSheetsFromBase64(string base64, IReadOnlyList<SheetRename> renames)
        {
            if (string.IsNullOrWhiteSpace(base64))
                throw new ArgumentException("base64 must not be null or empty.", nameof(base64));
            return RenameSheets(Convert.FromBase64String(base64), renames);
        }
    }
}
