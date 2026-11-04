using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using Excel = Microsoft.Office.Interop.Excel;

namespace GTA.XlsxLib
{
    public partial class XlsxExtractorTool
    {
        private static void ExtractDataWithInterop(
            string xlsxPath,
            string outputXmlPath,
            Dictionary<string, SheetPrecomputedData>? precomputed = null)
        {
            Excel.Application? excelApp  = null;
            Excel.Workbooks?   workbooks = null;
            Excel.Workbook?    workbook  = null;

            try
            {
                excelApp = new Excel.Application { Visible = false, DisplayAlerts = false };
                workbooks = excelApp.Workbooks;
                workbook  = workbooks.Open(xlsxPath);

                string fileNameNoExt = Path.GetFileNameWithoutExtension(xlsxPath);
                XElement xmlRoot = new XElement("Workbook",
                    new XAttribute("FileName", fileNameNoExt));

                foreach (Excel.Worksheet sheet in workbook.Worksheets)
                {
                    Console.WriteLine($"  [Sheet] Processing: {sheet.Name}");

                    XElement xmlSheet = new XElement("Worksheet",
                        new XAttribute("Name", SanitizeForXml(sheet.Name)));

                    Excel.Range usedRange = sheet.UsedRange;
                    int rowCount  = usedRange.Rows.Count;
                    int colCount  = usedRange.Columns.Count;
                    int startRow  = usedRange.Row;
                    int startCol  = usedRange.Column;

                    XElement xmlColumns = new XElement("Columns");
                    for (int c = 1; c <= colCount; c++)
                    {
                        int absoluteCol = startCol + c - 1;
                        try
                        {
                            Excel.Range colRange = (Excel.Range)sheet.Columns[absoluteCol];
                            long widthEmu = PointsToEmu(Convert.ToDouble(colRange.Width));
                            Marshal.ReleaseComObject(colRange);
                            xmlColumns.Add(new XElement("Column",
                                new XAttribute("Name",     GetColumnName(absoluteCol)),
                                new XAttribute("WidthEmu", widthEmu)));
                        }
                        catch { }
                    }
                    if (xmlColumns.HasElements) xmlSheet.Add(xmlColumns);

                    SheetPrecomputedData? sheetData = null;
                    precomputed?.TryGetValue(sheet.Name, out sheetData);
                    // sheetData carries: merges, cell formatting, row heights, pictures

                    var allMergeAreas  = sheetData?.AllMergeAddresses
                                         ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var mergeAnchorMap = sheetData?.MergeAnchorMap
                                         ?? new Dictionary<string, string>();

                    if (allMergeAreas.Count > 0)
                        Console.WriteLine(
                            $"    -> {allMergeAreas.Count} merge(s) (from ClosedXML pre-extract).");

                    object[,]? valueArray = usedRange.Value2 as object[,];
                    if (valueArray != null)
                    {
                        XElement xmlGridData = new XElement("GridData");

                        for (int r = 1; r <= rowCount; r++)
                        {
                            int absoluteRow = startRow + r - 1;

                            XElement xmlRow = new XElement("Row",
                                new XAttribute("Index", absoluteRow));

                            // Row height — from ClosedXML pre-extract (zero COM)
                            if (sheetData != null &&
                                sheetData.RowHeights.TryGetValue(absoluteRow, out long rh))
                                xmlRow.Add(new XAttribute("HeightEmu", rh));

                            bool rowHasData = false;

                            for (int c = 1; c <= colCount; c++)
                            {
                                object cellValue = valueArray[r, c];
                                if (cellValue == null ||
                                    string.IsNullOrWhiteSpace(cellValue.ToString()))
                                    continue;

                                int    absoluteCol = startCol + c - 1;
                                string sanitized   = SanitizeForXml(
                                    cellValue.ToString()!.Trim());
                                if (string.IsNullOrWhiteSpace(sanitized)) continue;

                                XElement xmlCell = new XElement("Cell",
                                    new XAttribute("Column", GetColumnName(absoluteCol)),
                                    sanitized);

                                string anchorKey = $"{absoluteRow},{absoluteCol}";

                                // Merge ref — in-memory dict, zero COM
                                if (mergeAnchorMap.TryGetValue(anchorKey, out string? mergeAddr))
                                    xmlCell.Add(new XAttribute("MergeRef", mergeAddr));

                                // Cell formatting — from ClosedXML pre-extract (zero COM)
                                if (sheetData != null &&
                                    sheetData.CellFormats.TryGetValue(anchorKey, out var fmt))
                                {
                                    if (fmt.Bold)          xmlCell.Add(new XAttribute("Bold",          "True"));
                                    if (fmt.Italic)        xmlCell.Add(new XAttribute("Italic",        "True"));
                                    if (fmt.Strikethrough) xmlCell.Add(new XAttribute("Strikethrough", "True"));
                                    if (fmt.Underline)     xmlCell.Add(new XAttribute("Underline",     "True"));
                                    if (fmt.FontColor != null) xmlCell.Add(new XAttribute("FontColor", fmt.FontColor));
                                    if (fmt.BgColor   != null) xmlCell.Add(new XAttribute("BgColor",   fmt.BgColor));
                                    if (fmt.HasPattern)    xmlCell.Add(new XAttribute("HasPattern",    "True"));
                                }

                                xmlRow.Add(xmlCell);
                                rowHasData = true;
                            }

                            if (rowHasData) xmlGridData.Add(xmlRow);
                        }

                        if (xmlGridData.HasElements) xmlSheet.Add(xmlGridData);
                    }

                    if (allMergeAreas.Count > 0)
                    {
                        XElement xmlMergedCells = new XElement("MergedCells");
                        foreach (string area in allMergeAreas)
                            xmlMergedCells.Add(new XElement("MergeArea",
                                new XAttribute("Range", area)));
                        xmlSheet.Add(xmlMergedCells);
                    }

                    Marshal.ReleaseComObject(usedRange);

                    Excel.Shapes shapes = sheet.Shapes;
                    if (shapes != null && shapes.Count > 0)
                    {
                        XElement xmlShapes = new XElement("Shapes");

                        for (int i = 1; i <= shapes.Count; i++)
                        {
                            Excel.Shape shape = shapes.Item(i);
                            try
                            {
                                bool isPicture = ((int)shape.Type == 13);
                                if (isPicture) continue; // finally releases the COM object

                                // Non-picture shape: read position + text safely
                                long leftEmu   = PointsToEmu(shape.Left);
                                long topEmu    = PointsToEmu(shape.Top);
                                long widthEmu  = PointsToEmu(shape.Width);
                                long heightEmu = PointsToEmu(shape.Height);

                                string cellRef = "";
                                try { cellRef = shape.TopLeftCell.Address[false, false]; }
                                catch { }

                                try
                                {
                                    var textFrame = shape.TextFrame;
                                    if (textFrame != null)
                                    {
                                        string text = SanitizeForXml(textFrame.Characters().Text);
                                        if (!string.IsNullOrWhiteSpace(text))
                                        {
                                            xmlShapes.Add(new XElement("Shape",
                                                new XAttribute("Name",      SanitizeForXml(shape.Name ?? "Shape")),
                                                new XAttribute("Cell",      cellRef),
                                                new XAttribute("LeftEmu",   leftEmu),
                                                new XAttribute("TopEmu",    topEmu),
                                                new XAttribute("WidthEmu",  widthEmu),
                                                new XAttribute("HeightEmu", heightEmu),
                                                text.Trim()));
                                        }
                                        Marshal.ReleaseComObject(textFrame);
                                    }
                                }
                                catch { }
                            }
                            catch { }
                            finally { Marshal.ReleaseComObject(shape); }
                        }

                        if (xmlShapes.HasElements) xmlSheet.Add(xmlShapes);
                        Marshal.ReleaseComObject(shapes);
                    }

                    if (sheetData?.Pictures.Count > 0)
                    {
                        XElement xmlImages = new XElement("Images");
                        foreach (ImagePositionEntry pic in sheetData.Pictures)
                        {
                            xmlImages.Add(new XElement("Image",
                                new XAttribute("Name",      pic.Name),
                                new XAttribute("Cell",      pic.CellRef),
                                new XAttribute("WidthEmu",  pic.WidthEmu),
                                new XAttribute("HeightEmu", pic.HeightEmu),
                                new XAttribute("File",      pic.SafeFileName + ".*")));
                        }
                        xmlSheet.Add(xmlImages);
                    }

                    if (xmlSheet.HasElements) xmlRoot.Add(xmlSheet);
                    Marshal.ReleaseComObject(sheet);
                }

                XDocument xDoc = new XDocument(
                    new XDeclaration("1.0", "utf-8", "yes"),
                    xmlRoot);
                xDoc.Save(outputXmlPath);
                Console.WriteLine($"  [XML] Saved: {Path.GetFileName(outputXmlPath)}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ERROR (Interop): {ex.Message}");
                Console.WriteLine($"  {ex.StackTrace}");
            }
            finally
            {
                if (workbook  != null) { workbook.Close(false); Marshal.ReleaseComObject(workbook); }
                if (workbooks != null) Marshal.ReleaseComObject(workbooks);
                if (excelApp  != null) { excelApp.Quit(); Marshal.ReleaseComObject(excelApp); }
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }

        /// <summary>Checks if the used range has ANY merged cells at all.</summary>
        private static bool CheckHasMerges(Excel.Range usedRange)
        {
            try
            {
                object mc = usedRange.MergeCells;
                // If it's a bool and it's false → definitely no merges
                if (mc is bool b && !b) return false;
                // DBNull means mixed → has some merges
                // true means all merged → has merges
                return true;
            }
            catch { return false; }
        }

        /// <summary>Safely checks if a single cell is part of a merged range.</summary>
        private static bool IsCellMerged(Excel.Range cell)
        {
            try
            {
                object mc = cell.MergeCells;
                if (mc == null || mc is DBNull) return false;
                return Convert.ToBoolean(mc);
            }
            catch { return false; }
        }
    }
}
