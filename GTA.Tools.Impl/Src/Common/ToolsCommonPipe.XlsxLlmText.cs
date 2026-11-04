using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace GTA.Tools.Impl.Common
{
    public static partial class ToolsCommonPipe
    {
        public const int MaxXlsxOutputTokens = 100_000;

        private const string XlsxHrThick = "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━";
        private const string XlsxHrThin  = "─────────────────────────────────────────────────────────────";
        private const long   XlsxEmuPerPx = 9525L; // 1 pixel at 96 DPI = 9,525 EMU

        public static List<string> GetXlsxSheetNames(string xmlContent)
        {
            try
            {
                var doc    = XDocument.Parse(xmlContent);
                var wbRoot = doc.Root;
                if (wbRoot == null) return new List<string>();

                return wbRoot.Elements("Worksheet")
                    .Select((s, i) => s.Attribute("Name")?.Value ?? $"Sheet{i + 1}")
                    .ToList();
            }
            catch
            {
                return new List<string>();
            }
        }

        private static int? ExtractRowFromCellRef(string? cellRef)
        {
            if (string.IsNullOrEmpty(cellRef)) return null;

            string first = cellRef.Split(':', '~')[0];
            int i = first.Length;
            while (i > 0 && char.IsDigit(first[i - 1])) i--;

            if (i == first.Length) return null; // no trailing digits at all
            string digits = first.Substring(i);
            return int.TryParse(digits, out int n) ? n : (int?)null;
        }

        public static string BuildXlsxLlmOutput(
            string  xmlContent,
            string  fileLabel,
            string? requestedSheet,
            string  toolName,
            string  imageToolName,
            string  imageIdParamName,
            string  imageIdParamValue,
            int     maxTokens = MaxXlsxOutputTokens,
            int     rowStart  = 0,
            int?    rowLength = null)
        {
            if (string.IsNullOrWhiteSpace(xmlContent))
                return $"❌ {toolName} — No content was extracted from {fileLabel}.\n" +
                       "The file may be empty, password-protected, or unsupported.";

            List<string> sheetNames = GetXlsxSheetNames(xmlContent);

            if (!string.IsNullOrWhiteSpace(requestedSheet))
            {
                string? match = sheetNames.FirstOrDefault(
                    n => string.Equals(n, requestedSheet, StringComparison.OrdinalIgnoreCase));

                if (match == null)
                {
                    var sb = new StringBuilder();
                    sb.AppendLine($"❌ {toolName} — Sheet \"{requestedSheet}\" was not found in {fileLabel}.");
                    sb.AppendLine($"Available sheet(s) ({sheetNames.Count}):");
                    foreach (string n in sheetNames)
                        sb.AppendLine($"  • {n}");
                    sb.AppendLine();
                    sb.AppendLine($"Call {toolName} again with the exact sheet name from the list above " +
                                  "(spelled and capitalized exactly as shown).");
                    return sb.ToString().TrimEnd();
                }

                return XlsxXmlToLlmText(
                    xmlContent, fileLabel, match,
                    imageToolName, imageIdParamName, imageIdParamValue,
                    rowStart, rowLength);
            }

            string fullText = XlsxXmlToLlmText(
                xmlContent, fileLabel, onlySheetName: null,
                imageToolName, imageIdParamName, imageIdParamValue,
                rowStart, rowLength);

            int estimatedTokens = EstimateTokenCount(fullText);
            if (estimatedTokens <= maxTokens)
                return fullText;

            var fb = new StringBuilder();
            fb.AppendLine(XlsxHrThick);
            fb.AppendLine($"  ⚠️ {fileLabel} is a SUPER HUGE workbook — too big to return in one response.");
            fb.AppendLine($"  Estimated ~{estimatedTokens:N0} tokens, which exceeds the {maxTokens:N0}-token " +
                          $"limit for a single {toolName} response.");
            fb.AppendLine("  It is NOT possible to dump the entire workbook as one block of text.");
            fb.AppendLine(XlsxHrThick);
            fb.AppendLine();
            fb.AppendLine($"  This workbook has {sheetNames.Count} sheet(s). Read them ONE BY ONE instead " +
                          "by passing the 'sheet' parameter:");
            fb.AppendLine();
            for (int i = 0; i < sheetNames.Count; i++)
                fb.AppendLine($"    {i + 1}. \"{sheetNames[i]}\"");
            fb.AppendLine();
            fb.AppendLine($"  ACTION REQUIRED: Call {toolName} again, this time passing the exact sheet name, e.g.:");
            fb.AppendLine($"    {toolName}(..., sheet: \"{(sheetNames.Count > 0 ? sheetNames[0] : "Sheet1")}\")");
            fb.AppendLine();
            fb.AppendLine("  ALTERNATIVE: if a single sheet is itself too big, narrow further with a row");
            fb.AppendLine("  range instead of pulling the whole sheet at once, e.g.:");
            fb.AppendLine($"    {toolName}(..., sheet: \"{(sheetNames.Count > 0 ? sheetNames[0] : "Sheet1")}\", " +
                          "row_start: 0, row_length: 200)");
            fb.AppendLine("  row_start is 0-based (0 = Excel row 1); row_length is how many rows to include");
            fb.AppendLine("  and is automatically clamped if it runs past the sheet's actual last row.");
            fb.AppendLine();
            fb.AppendLine("  IMPORTANT: Never give up looking for the information just because the workbook");
            fb.AppendLine("  is huge — one of the sheets listed above is very likely the one you need. Go");
            fb.AppendLine("  through them one at a time (using the sheet names above) until you find it.");
            fb.AppendLine(XlsxHrThick);

            return fb.ToString().TrimEnd();
        }

        public static string XlsxXmlToLlmText(
            string  xmlContent,
            string  fileLabel,
            string? onlySheetName     = null,
            string? imageToolName     = null,
            string? imageIdParamName  = null,
            string? imageIdParamValue = null,
            int     rowStart          = 0,
            int?    rowLength         = null)
        {
            var sb = new StringBuilder();

            try
            {
                var doc    = XDocument.Parse(xmlContent);
                var wbRoot = doc.Root;
                if (wbRoot == null) return "(empty workbook XML)";

                var sheets     = wbRoot.Elements("Worksheet").ToList();
                int sheetCount = sheets.Count;

                int totalImages = sheets
                    .Sum(s => s.Element("Images")?.Elements("Image").Count() ?? 0);

                string imgHdr = totalImages > 0 ? $", {totalImages} image(s)" : "";
                sb.AppendLine(XlsxHrThick);
                if (!string.IsNullOrEmpty(onlySheetName))
                    sb.AppendLine($"  Excel workbook ({fileLabel}) — showing sheet \"{onlySheetName}\" " +
                                  $"({sheetCount} sheet(s) total in workbook{imgHdr})");
                else
                    sb.AppendLine($"  Excel workbook ({fileLabel}) — {sheetCount} sheet(s){imgHdr}");
                if (totalImages > 0)
                    sb.AppendLine("  📷 Embedded images detected — see footer for fetch instructions.");
                sb.AppendLine(XlsxHrThick);

                int globalImgIdx = 0; // 1-based, matches *_read_xlsx_image indices

                bool rowFilterActive = rowStart != 0 || rowLength.HasValue;
                int  safeRowStart    = Math.Max(0, rowStart);
                int  wantedRowStart  = safeRowStart + 1; // 0-based param → 1-based Excel row

                for (int si = 0; si < sheetCount; si++)
                {
                    XElement sheet     = sheets[si];
                    string   sheetName = sheet.Attribute("Name")?.Value ?? $"Sheet{si + 1}";

                    bool isTarget = string.IsNullOrEmpty(onlySheetName) ||
                                    string.Equals(sheetName, onlySheetName, StringComparison.OrdinalIgnoreCase);

                    XElement? gridData   = sheet.Element("GridData");
                    XElement? shapesElem = sheet.Element("Shapes");
                    XElement? imagesElem = sheet.Element("Images");

                    int effectiveRowStart = wantedRowStart;
                    int effectiveRowEnd   = int.MaxValue;

                    if (isTarget && rowFilterActive)
                    {
                        var gridRowsAll = gridData?.Elements("Row").ToList() ?? new List<XElement>();
                        int gridMaxRow = gridRowsAll.Count > 0
                            ? gridRowsAll.Max(r => int.TryParse(r.Attribute("Index")?.Value, out int gi) ? gi : 0)
                            : 0;

                        int shapesMaxRow = shapesElem?.Elements("Shape")
                            .Select(s => ExtractRowFromCellRef(s.Attribute("Cell")?.Value) ?? 0)
                            .DefaultIfEmpty(0).Max() ?? 0;

                        int imagesMaxRow = imagesElem?.Elements("Image")
                            .Select(im => ExtractRowFromCellRef(im.Attribute("Cell")?.Value) ?? 0)
                            .DefaultIfEmpty(0).Max() ?? 0;

                        int sheetMaxRowAny = Math.Max(gridMaxRow, Math.Max(shapesMaxRow, imagesMaxRow));

                        effectiveRowEnd = rowLength.HasValue
                            ? Math.Min(effectiveRowStart + Math.Max(0, rowLength.Value) - 1, sheetMaxRowAny)
                            : sheetMaxRowAny;

                        if (effectiveRowEnd < effectiveRowStart)
                            effectiveRowEnd = effectiveRowStart - 1; // explicit empty range, handled gracefully
                    }

                    if (isTarget)
                    {
                        sb.AppendLine();
                        string heading = $"── Sheet {si + 1}/{sheetCount} : \"{sheetName}\" ";
                        int    padLen  = Math.Max(0, XlsxHrThin.Length - heading.Length);
                        sb.AppendLine(heading + new string('─', padLen));

                        if (rowFilterActive)
                        {
                            string endDisplay = effectiveRowEnd == int.MaxValue ? "(sheet has no rows)" : effectiveRowEnd.ToString();
                            sb.AppendLine($"  📍 Row filter active → showing Excel rows {effectiveRowStart}–{endDisplay} " +
                                          $"(row_start={rowStart}, row_length={(rowLength?.ToString() ?? "null (to end)")}). " +
                                          "Out-of-range requests are auto-clamped, never an error.");
                        }

                        sb.AppendLine();
                    }

                    bool hasContent = false;

                    if (gridData != null)
                    {
                        foreach (XElement row in gridData.Elements("Row"))
                        {
                            int rowIdx = int.TryParse(row.Attribute("Index")?.Value, out int ri) ? ri : 0;

                            bool rowInRange = !rowFilterActive ||
                                               (rowIdx >= effectiveRowStart && rowIdx <= effectiveRowEnd);

                            foreach (XElement cell in row.Elements("Cell"))
                            {
                                string col      = cell.Attribute("Column")?.Value  ?? "";
                                string mergeRef = cell.Attribute("MergeRef")?.Value ?? "";
                                string val      = cell.Value.Trim();

                                if (string.IsNullOrWhiteSpace(val)) continue;

                                string cellRef = !string.IsNullOrEmpty(mergeRef)
                                    ? mergeRef.Replace(":", "~")
                                    : $"{col}{rowIdx}";

                                string display = val
                                    .Replace("\r\n", " ↵ ")
                                    .Replace("\r",   " ↵ ")
                                    .Replace("\n",   " ↵ ");

                                if (display.Length > 300)
                                    display = display.Substring(0, 297) + "…";

                                if (isTarget && rowInRange)
                                    sb.AppendLine($"{cellRef,-8}: {display}");
                                if (rowInRange)
                                    hasContent = true;
                            }
                        }
                    }

                    if (shapesElem != null)
                    {
                        foreach (XElement shape in shapesElem.Elements("Shape"))
                        {
                            string shapeName = shape.Attribute("Name")?.Value  ?? "Shape";
                            string shapeCell = shape.Attribute("Cell")?.Value  ?? "";
                            long   wEmu      = long.TryParse(shape.Attribute("WidthEmu")?.Value,  out long sw) ? sw : 0L;
                            long   hEmu      = long.TryParse(shape.Attribute("HeightEmu")?.Value, out long sh) ? sh : 0L;
                            string shapeText = shape.Value.Trim();

                            int wPx = (int)(wEmu / XlsxEmuPerPx);
                            int hPx = (int)(hEmu / XlsxEmuPerPx);

                            int?  shapeRow   = ExtractRowFromCellRef(shapeCell);
                            bool  rowInRange = !rowFilterActive || !shapeRow.HasValue ||
                                               (shapeRow.Value >= effectiveRowStart && shapeRow.Value <= effectiveRowEnd);

                            if (isTarget && rowInRange)
                            {
                                string posStr  = !string.IsNullOrEmpty(shapeCell) ? $" @ {shapeCell}" : "";
                                string sizeStr = (wPx > 0 || hPx > 0) ? $"  ({wPx}×{hPx} px)" : "";

                                sb.AppendLine();
                                sb.AppendLine($"  Shape \"{shapeName}\"{posStr}{sizeStr}");

                                if (!string.IsNullOrWhiteSpace(shapeText))
                                {
                                    foreach (string line in shapeText
                                        .Replace("\r\n", "\n").Replace("\r", "\n")
                                        .Split('\n'))
                                    {
                                        string trimmed = line.TrimEnd();
                                        if (!string.IsNullOrEmpty(trimmed))
                                            sb.AppendLine($"    {trimmed}");
                                    }
                                }
                            }

                            if (rowInRange)
                                hasContent = true;
                        }
                    }

                    if (imagesElem != null)
                    {
                        bool firstImg = true;
                        foreach (XElement img in imagesElem.Elements("Image"))
                        {
                            globalImgIdx++;

                            if (!isTarget) continue;

                            string imgName = img.Attribute("Name")?.Value  ?? "Image";
                            string imgCell = img.Attribute("Cell")?.Value  ?? "";
                            long   wEmu    = long.TryParse(img.Attribute("WidthEmu")?.Value,  out long iw) ? iw : 0L;
                            long   hEmu    = long.TryParse(img.Attribute("HeightEmu")?.Value, out long ih) ? ih : 0L;

                            int?  imgRow     = ExtractRowFromCellRef(imgCell);
                            bool  rowInRange = !rowFilterActive || !imgRow.HasValue ||
                                               (imgRow.Value >= effectiveRowStart && imgRow.Value <= effectiveRowEnd);

                            if (!rowInRange) continue; // skip display, index already counted above

                            int wPx = (int)(wEmu / XlsxEmuPerPx);
                            int hPx = (int)(hEmu / XlsxEmuPerPx);

                            if (firstImg) { sb.AppendLine(); firstImg = false; }

                            string posStr  = !string.IsNullOrEmpty(imgCell) ? $" @ {imgCell}" : "";
                            string sizeStr = (wPx > 0 || hPx > 0) ? $"  ({wPx}×{hPx} px)" : "";

                            sb.AppendLine($"  Image [{globalImgIdx}] \"{imgName}\"{posStr}{sizeStr}");
                            hasContent = true;
                        }
                    }

                    if (isTarget && !hasContent)
                        sb.AppendLine(rowFilterActive
                            ? "  (no cell values, shapes, or images found in the requested row range)"
                            : "  (empty — no cell values, shapes, or images)");
                }

                if (totalImages > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine(XlsxHrThick);
                    sb.AppendLine($"  💡 {totalImages} embedded image(s) found across the whole workbook.");

                    if (!string.IsNullOrEmpty(imageToolName) &&
                        !string.IsNullOrEmpty(imageIdParamName) &&
                        !string.IsNullOrEmpty(imageIdParamValue))
                    {
                        sb.AppendLine($"     To inspect them, call {imageToolName}:");
                        sb.AppendLine($"       Manifest  →  {imageToolName}(" +
                                      $"{imageIdParamName}: \"{imageIdParamValue}\")");
                        sb.AppendLine($"       Load one  →  {imageToolName}(" +
                                      $"{imageIdParamName}: \"{imageIdParamValue}\", image_indices: \"1\")");
                        sb.AppendLine($"       Load many →  {imageToolName}(" +
                                      $"{imageIdParamName}: \"{imageIdParamValue}\", image_indices: \"1,2,5\")");
                    }
                    else
                    {
                        sb.AppendLine("     Call the matching *_read_xlsx_image tool for this source to view them.");
                    }
                    sb.AppendLine(XlsxHrThick);
                }
            }
            catch (Exception ex)
            {
                // Fallback: parse failed — return raw XML so the LLM still has something
                sb.AppendLine($"⚠️ XlsxXmlToLlmText parse error: {ex.Message}");
                sb.AppendLine("Returning raw workbook XML as fallback:");
                sb.AppendLine();
                sb.AppendLine(xmlContent);
            }

            return sb.ToString().TrimEnd();
        }
    }
}
