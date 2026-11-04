using System;
using System.Linq;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using P = DocumentFormat.OpenXml.Presentation;
using A = DocumentFormat.OpenXml.Drawing;

namespace PptxModifierLib
{
    public static class PptxTableTool
    {
        private const string TableGraphicDataUri = "http://schemas.openxmlformats.org/drawingml/2006/table";

        private const string DefaultTableStyleId = "{5C22544A-7EE6-4342-B048-85BDC9FD1C3A}";

        public static PptxOperationResult SetTable(
            byte[] pptxBytes,
            int slideNumber,
            string shapeName,
            double top,
            double left,
            double bottom,
            double right,
            string[][] rows,
            bool hasHeaderRow = true,
            double[]? columnWidths = null)
        {
            var report = new StringBuilder();
            report.AppendLine("--- pptx_table ---");

            if (pptxBytes == null || pptxBytes.Length == 0)
            {
                report.AppendLine("❌ No presentation bytes supplied.");
                return Fail(report);
            }
            if (string.IsNullOrWhiteSpace(shapeName))
            {
                report.AppendLine("❌ shapeName must not be empty — it is the stable key used to find/replace this table on later calls.");
                return Fail(report);
            }
            if (right <= left || bottom <= top)
            {
                report.AppendLine("❌ right must be greater than left, and bottom must be greater than top (normalized fractions of the slide's size).");
                return Fail(report);
            }
            if (rows == null || rows.Length == 0)
            {
                report.AppendLine("❌ rows must contain at least one row.");
                return Fail(report);
            }
            if (rows.Any(r => r == null))
            {
                report.AppendLine("❌ rows must not contain a null row.");
                return Fail(report);
            }

            double width = right - left;
            double height = bottom - top;

            int columnCount = rows[0].Length;
            if (columnCount == 0)
            {
                report.AppendLine("❌ rows[0] must contain at least one column.");
                return Fail(report);
            }
            if (rows.Any(r => r.Length != columnCount))
            {
                report.AppendLine($"❌ rows is jagged — every row must have the same number of columns (expected {columnCount}, based on rows[0]).");
                return Fail(report);
            }

            double[] normalizedColWidths;
            if (columnWidths != null)
            {
                if (columnWidths.Length != columnCount)
                {
                    report.AppendLine($"❌ columnWidths has {columnWidths.Length} entries but rows has {columnCount} column(s) — lengths must match.");
                    return Fail(report);
                }
                if (columnWidths.Any(w => w <= 0))
                {
                    report.AppendLine("❌ columnWidths entries must all be positive.");
                    return Fail(report);
                }
                double sum = columnWidths.Sum();
                if (Math.Abs(sum - width) > 0.0001)
                {
                    report.AppendLine($"❌ columnWidths sums to {sum:0.####} but (right - left) is {width:0.####} — they must match exactly (adjust one or the other).");
                    return Fail(report);
                }
                normalizedColWidths = columnWidths;
            }
            else
            {
                normalizedColWidths = SplitEvenly(width, columnCount);
            }

            try
            {
                bool created = false;
                long offsetXEmu = 0, offsetYEmu = 0, widthEmu = 0, heightEmu = 0;

                byte[] result = PptxCommon.EditInMemory(pptxBytes, doc =>
                {
                    PresentationPart presentationPart = doc.PresentationPart
                        ?? throw new InvalidOperationException("this file has no presentation part — is it a valid .pptx?");

                    var slides = PptxCommon.GetOrderedSlideParts(presentationPart);
                    if (slideNumber < 1 || slideNumber > slides.Count)
                        throw new InvalidOperationException(
                            $"slideNumber {slideNumber} is out of range — this presentation has {slides.Count} slide(s).");

                    SlidePart slidePart = slides[slideNumber - 1];
                    P.ShapeTree shapeTree = slidePart.Slide.CommonSlideData?.ShapeTree
                        ?? throw new InvalidOperationException("this slide has no shape tree (malformed slide XML).");

                    var (slideWidthEmu, slideHeightEmu) = PptxNormalizedCoordinates.GetSlideSizeEmu(presentationPart);
                    (offsetXEmu, offsetYEmu, widthEmu, heightEmu) = PptxNormalizedCoordinates.RectFromEdgesToEmu(
                        top, left, bottom, right, slideWidthEmu, slideHeightEmu);

                    long[] colWidthsEmu = normalizedColWidths
                        .Select(w => PptxNormalizedCoordinates.LengthToEmuByWidth(w, slideWidthEmu))
                        .ToArray();
                    long[] rowHeightsEmu = SplitEvenly(heightEmu, rows.Length);

                    A.Graphic graphic = BuildTableGraphic(rows, hasHeaderRow, colWidthsEmu, rowHeightsEmu);
                    var xfrm = new P.Transform(
                        new A.Offset { X = offsetXEmu, Y = offsetYEmu },
                        new A.Extents { Cx = widthEmu, Cy = heightEmu });

                    P.GraphicFrame? existingFrame = FindTableByName(shapeTree, shapeName);

                    if (existingFrame != null)
                    {
                        existingFrame.Transform = xfrm;
                        existingFrame.Graphic = graphic;
                        created = false;
                    }
                    else
                    {
                        uint newId = PptxCommon.NextShapeId(shapeTree);

                        var newFrame = new P.GraphicFrame(
                            new P.NonVisualGraphicFrameProperties(
                                new P.NonVisualDrawingProperties { Id = newId, Name = shapeName },
                                new P.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoGrouping = true }),
                                new P.ApplicationNonVisualDrawingProperties()),
                            xfrm,
                            graphic);

                        shapeTree.Append(newFrame);
                        created = true;
                    }

                    slidePart.Slide.Save();
                });

                report.AppendLine(created
                    ? $"✅ Created table \"{shapeName}\" on slide {slideNumber} — {rows.Length} row(s) x {columnCount} column(s), normalized edges top={top:0.####},left={left:0.####},bottom={bottom:0.####},right={right:0.####} -> EMU pos ({offsetXEmu},{offsetYEmu}), size ({widthEmu}x{heightEmu}){(hasHeaderRow ? ", header row bolded" : "")}."
                    : $"✅ Updated table \"{shapeName}\" on slide {slideNumber} — {rows.Length} row(s) x {columnCount} column(s), normalized edges top={top:0.####},left={left:0.####},bottom={bottom:0.####},right={right:0.####} -> EMU pos ({offsetXEmu},{offsetYEmu}), size ({widthEmu}x{heightEmu}){(hasHeaderRow ? ", header row bolded" : "")}.");

                return new PptxOperationResult { Success = true, ModifiedBytes = result, ReportText = report.ToString().TrimEnd() };
            }
            catch (Exception ex)
            {
                report.AppendLine($"❌ Fatal error: {ex.Message}");
                return Fail(report);
            }
        }

        private static PptxOperationResult Fail(StringBuilder report)
            => new PptxOperationResult { Success = false, ModifiedBytes = null, ReportText = report.ToString().TrimEnd() };

        private static double[] SplitEvenly(double total, int count)
        {
            var result = new double[count];
            for (int i = 0; i < count; i++)
                result[i] = total / count;
            return result;
        }

        private static long[] SplitEvenly(long total, int count)
        {
            long baseValue = total / count;
            long remainder = total % count;
            var result = new long[count];
            for (int i = 0; i < count; i++)
                result[i] = baseValue + (i < remainder ? 1 : 0);
            return result;
        }

        private static A.Graphic BuildTableGraphic(
            string[][] rows, bool hasHeaderRow, long[] colWidths, long[] rowHeights)
        {
            var tableGrid = new A.TableGrid();
            foreach (long w in colWidths)
                tableGrid.Append(new A.GridColumn { Width = w });

            var table = new A.Table();
            var tableProps = new A.TableProperties { FirstRow = hasHeaderRow, BandRow = true };
            tableProps.Append(new A.TableStyleId(DefaultTableStyleId));
            table.Append(tableProps);
            table.Append(tableGrid);

            for (int r = 0; r < rows.Length; r++)
            {
                var tableRow = new A.TableRow { Height = rowHeights[r] };
                bool isHeaderRow = hasHeaderRow && r == 0;

                foreach (string? cellText in rows[r])
                {
                    var paragraph = new A.Paragraph();
                    string text = cellText ?? string.Empty;
                    if (text.Length > 0 || true)
                    {
                        var run = isHeaderRow
                            ? new A.Run(new A.RunProperties { Bold = true }, new A.Text(text))
                            : new A.Run(new A.Text(text));
                        paragraph.Append(run);
                    }

                    var cellTextBody = new A.TextBody(new A.BodyProperties(), new A.ListStyle(), paragraph);
                    var tableCell = new A.TableCell(cellTextBody, new A.TableCellProperties());
                    tableRow.Append(tableCell);
                }

                table.Append(tableRow);
            }

            var graphicData = new A.GraphicData(table) { Uri = TableGraphicDataUri };
            return new A.Graphic(graphicData);
        }

        private static P.GraphicFrame? FindTableByName(P.ShapeTree shapeTree, string name)
        {
            foreach (P.GraphicFrame frame in shapeTree.Elements<P.GraphicFrame>())
            {
                string? n = frame.NonVisualGraphicFrameProperties?.NonVisualDrawingProperties?.Name?.Value;
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
                    return frame;
            }
            return null;
        }

        /// <summary>Base64 convenience wrapper — mirrors XlsxModifierTool's *FromBase64 pattern.</summary>
        public static PptxOperationResult SetTableFromBase64(
            string base64,
            int slideNumber,
            string shapeName,
            double top,
            double left,
            double bottom,
            double right,
            string[][] rows,
            bool hasHeaderRow = true,
            double[]? columnWidths = null)
        {
            if (string.IsNullOrWhiteSpace(base64))
                throw new ArgumentException("base64 must not be null or empty.", nameof(base64));
            return SetTable(Convert.FromBase64String(base64), slideNumber, shapeName,
                top, left, bottom, right, rows, hasHeaderRow, columnWidths);
        }
    }
}
