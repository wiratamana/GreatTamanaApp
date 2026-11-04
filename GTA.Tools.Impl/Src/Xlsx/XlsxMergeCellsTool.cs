using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ClosedXML.Excel;

namespace GTA.XlsxLib
{

    /// <summary>One merge/unmerge instruction for a given sheet + range address.</summary>
    public sealed class MergeOp
    {
        public string Sheet        { get; init; } = string.Empty;
        public string RangeAddress { get; init; } = string.Empty;
        public bool   Merge        { get; init; } = true;
    }

    public static partial class XlsxModifierTool
    {
        /// <summary>(3.9) Merges or unmerges one or more ranges.</summary>
        public static XlsxOperationResult MergeCells(byte[] xlsxBytes, IReadOnlyList<MergeOp> ops)
        {
            var report = new StringBuilder();
            report.AppendLine("--- xlsx_merge_cells ---");

            int total = ops?.Count ?? 0;
            if (xlsxBytes == null || xlsxBytes.Length == 0 || ops == null || total == 0)
            {
                report.AppendLine("❌ No merge/unmerge operations supplied.");
                return new XlsxOperationResult { Success = false, ModifiedBytes = null, ReportText = report.ToString().TrimEnd() };
            }

            try
            {
                xlsxBytes = XlsxCompatSanitizer.EnsureLoadable(xlsxBytes);
                using var ms = new MemoryStream(xlsxBytes, writable: false);
                using var workbook = new XLWorkbook(ms);

                int applied = 0;

                foreach (MergeOp op in ops)
                {
                    string sheetName = op.Sheet ?? "";
                    string rangeAddr = op.RangeAddress ?? "";

                    if (!TryGetSheet(workbook, sheetName, out IXLWorksheet? ws) || ws == null)
                    {
                        report.AppendLine($"❌ {sheetName}!{rangeAddr}: sheet \"{sheetName}\" not found");
                        continue;
                    }

                    string refDisplay = rangeAddr.Replace(":", "~");
                    try
                    {
                        IXLRange range = ws.Range(rangeAddr);
                        if (op.Merge) range.Merge(); else range.Unmerge();

                        string action = op.Merge ? "merged" : "unmerged";
                        report.AppendLine($"✅ {sheetName}!{refDisplay} {action}");
                        applied++;
                    }
                    catch (Exception ex)
                    {
                        report.AppendLine($"❌ {sheetName}!{refDisplay}: {ex.Message}");
                    }
                }

                using var outMs = new MemoryStream();
                workbook.SaveAs(outMs);

                report.AppendLine($"{applied} of {total} operation(s) applied.");
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

        public static XlsxOperationResult MergeCellsFromBase64(string base64, IReadOnlyList<MergeOp> ops)
        {
            if (string.IsNullOrWhiteSpace(base64))
                throw new ArgumentException("base64 must not be null or empty.", nameof(base64));
            return MergeCells(Convert.FromBase64String(base64), ops);
        }
    }
}
