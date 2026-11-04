using System;
using System.Collections.Generic;
using System.IO;
using ClosedXML.Excel;
using ClosedXML.Excel.Drawings;

namespace GTA.XlsxLib
{
    public partial class XlsxExtractorTool
    {

        // Per-cell formatting carried from ClosedXML → GridData loop
        private readonly struct CellFormatEntry
        {
            public bool    Bold          { get; init; }
            public bool    Italic        { get; init; }
            public bool    Strikethrough { get; init; }
            public bool    Underline     { get; init; }
            public string? FontColor     { get; init; }  // "#RRGGBB" or null
            public string? BgColor       { get; init; }  // "#RRGGBB" or null
            public bool    HasPattern    { get; init; }
            public bool    IsNonDefault  => Bold || Italic || Strikethrough ||
                                            Underline || FontColor != null ||
                                            BgColor != null || HasPattern;
        }

        // Picture position from ClosedXML → <Images> section in XML
        private sealed class ImagePositionEntry
        {
            public string Name         { get; init; } = "";
            public string CellRef      { get; init; } = "";
            public long   WidthEmu     { get; init; }
            public long   HeightEmu    { get; init; }
            public string SafeFileName { get; init; } = "";
        }

        // All pre-extracted data for ONE worksheet
        private sealed class SheetPrecomputedData
        {
            // Merges: key = "anchorRow,anchorCol" → merge address "B2:J2"
            public Dictionary<string, string> MergeAnchorMap   { get; } = new();
            public HashSet<string>            AllMergeAddresses { get; } =
                new(StringComparer.OrdinalIgnoreCase);

            // Cell formatting: key = "row,col" (1-based absolute)
            public Dictionary<string, CellFormatEntry> CellFormats { get; } = new();

            // Row heights in EMU: key = 1-based row number (only non-default heights)
            public Dictionary<int, long> RowHeights { get; } = new();

            // Picture positions for <Images> XML section
            public List<ImagePositionEntry> Pictures { get; } = new();
        }

        private static string ExtractXmlFromBytes(byte[] xlsxBytes)
        {
            var precomputed = PreExtractWithClosedXml(xlsxBytes);

            string sessionId = Guid.NewGuid().ToString("N");
            string tempXlsx  = Path.Combine(Path.GetTempPath(), $"xlsx_{sessionId}.xlsx");
            string tempXml   = Path.Combine(Path.GetTempPath(), $"xlsx_{sessionId}.xml");

            try
            {
                File.WriteAllBytes(tempXlsx, xlsxBytes);
                ExtractDataWithInterop(tempXlsx, tempXml, precomputed);
                return File.Exists(tempXml) ? File.ReadAllText(tempXml) : string.Empty;
            }
            finally
            {
                try { File.Delete(tempXlsx); } catch { /* best-effort */ }
                try { File.Delete(tempXml);  } catch { /* best-effort */ }
            }
        }

        private static Dictionary<string, SheetPrecomputedData>
            PreExtractWithClosedXml(byte[] xlsxBytes)
        {
            var result = new Dictionary<string, SheetPrecomputedData>(
                StringComparer.OrdinalIgnoreCase);
            try
            {
                using var ms       = new MemoryStream(XlsxCompatSanitizer.EnsureLoadable(xlsxBytes), writable: false);
                using var workbook = new XLWorkbook(ms);
                double defaultRowHt = workbook.Style?.Font == null ? 15.0 : 15.0; // fallback

                foreach (IXLWorksheet ws in workbook.Worksheets)
                {
                    var data = new SheetPrecomputedData();

                    foreach (IXLRange range in ws.MergedRanges)
                    {
                        try
                        {
                            IXLCell anchor = range.FirstCell();
                            string  key    = $"{anchor.Address.RowNumber}," +
                                             $"{anchor.Address.ColumnNumber}";
                            string  addr   = range.RangeAddress.ToString();
                            data.MergeAnchorMap[key] = addr;
                            data.AllMergeAddresses.Add(addr);
                        }
                        catch { }
                    }

                    foreach (IXLCell cell in ws.CellsUsed(XLCellsUsedOptions.Contents))
                    {
                        try
                        {
                            var    style = cell.Style;
                            string key   = $"{cell.Address.RowNumber}," +
                                           $"{cell.Address.ColumnNumber}";

                            bool bold   = style.Font.Bold;
                            bool italic = style.Font.Italic;
                            bool strike = style.Font.Strikethrough;
                            bool under  = style.Font.Underline != XLFontUnderlineValues.None;

                            string? fontHex = null;
                            try
                            {
                                // Use .Color directly — throws if no color set; catch is the guard.
                                // Skip if the resolved hex is pure black (default font color).
                                var c = style.Font.FontColor.Color;
                                string h = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
                                if (h != "#000000") fontHex = h;
                            }
                            catch { }

                            string? bgHex   = null;
                            bool    hasPatt = false;
                            try
                            {
                                var fill = style.Fill;
                                // XLFillPatternValues.None = no explicit pattern defined → skip
                                if (fill.PatternType != XLFillPatternValues.None)
                                {
                                    hasPatt = true;
                                    // Skip if resolved color is white (default background)
                                    var c = fill.BackgroundColor.Color;
                                    string h = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
                                    if (h != "#FFFFFF") bgHex = h;
                                }
                            }
                            catch { }

                            var entry = new CellFormatEntry
                            {
                                Bold          = bold,
                                Italic        = italic,
                                Strikethrough = strike,
                                Underline     = under,
                                FontColor     = fontHex,
                                BgColor       = bgHex,
                                HasPattern    = hasPatt,
                            };
                            if (entry.IsNonDefault)
                                data.CellFormats[key] = entry;
                        }
                        catch { }
                    }

                    double wsDefaultHt = ws.RowHeight;
                    foreach (IXLRow row in ws.RowsUsed())
                    {
                        try
                        {
                            if (Math.Abs(row.Height - wsDefaultHt) > 0.01)
                                data.RowHeights[row.RowNumber()] =
                                    (long)Math.Round(row.Height * EmuPerPoint);
                        }
                        catch { }
                    }

                    foreach (IXLPicture pic in ws.Pictures)
                    {
                        try
                        {
                            string cellRef  = pic.TopLeftCell.Address.ToString();
                            string rawName  = $"{ws.Name}_{cellRef}_{pic.Name}";
                            string safeName = MakeSafeFileName(rawName);

                            data.Pictures.Add(new ImagePositionEntry
                            {
                                Name         = SanitizeForXml(pic.Name ?? ""),
                                CellRef      = cellRef,
                                WidthEmu     = (long)(pic.Width  * EmuPerPixel),
                                HeightEmu    = (long)(pic.Height * EmuPerPixel),
                                SafeFileName = safeName,
                            });
                        }
                        catch { }
                    }

                    result[ws.Name] = data;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[XlsxExtractor] Pre-extraction warning: {ex.Message}");
            }
            return result;
        }
    }
}
