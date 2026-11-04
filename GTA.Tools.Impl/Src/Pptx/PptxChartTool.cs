using System;
using System.Globalization;
using System.Linq;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using P = DocumentFormat.OpenXml.Presentation;
using A = DocumentFormat.OpenXml.Drawing;
using C = DocumentFormat.OpenXml.Drawing.Charts;

namespace PptxModifierLib
{
    public static class PptxChartTool
    {
        private const string ChartGraphicDataUri = "http://schemas.openxmlformats.org/drawingml/2006/chart";

        private const uint CategoryAxisId = 111111111;
        private const uint ValueAxisId = 222222222;

        public static PptxOperationResult SetChart(
            byte[] pptxBytes,
            int slideNumber,
            string shapeName,
            double top,
            double left,
            double bottom,
            double right,
            string chartType,
            string[] categories,
            (string Name, double[] Values)[] series,
            string? title = null,
            bool showLegend = true)
        {
            var report = new StringBuilder();
            report.AppendLine("--- pptx_chart ---");

            if (pptxBytes == null || pptxBytes.Length == 0)
            {
                report.AppendLine("❌ No presentation bytes supplied.");
                return Fail(report);
            }
            if (string.IsNullOrWhiteSpace(shapeName))
            {
                report.AppendLine("❌ shapeName must not be empty — it is the stable key used to find/replace this chart on later calls.");
                return Fail(report);
            }
            if (right <= left || bottom <= top)
            {
                report.AppendLine("❌ right must be greater than left, and bottom must be greater than top (normalized fractions of the slide's size).");
                return Fail(report);
            }

            string normalizedChartType = (chartType ?? "").Trim().ToLowerInvariant();
            if (normalizedChartType != "line" && normalizedChartType != "bar" && normalizedChartType != "column")
            {
                report.AppendLine($"❌ chartType \"{chartType}\" is not recognized — expected \"line\", \"bar\", or \"column\".");
                return Fail(report);
            }

            if (categories == null || categories.Length == 0)
            {
                report.AppendLine("❌ categories must contain at least one entry.");
                return Fail(report);
            }
            if (series == null || series.Length == 0)
            {
                report.AppendLine("❌ series must contain at least one (Name, Values) entry.");
                return Fail(report);
            }
            foreach (var s in series)
            {
                if (string.IsNullOrWhiteSpace(s.Name))
                {
                    report.AppendLine("❌ every series must have a non-empty Name.");
                    return Fail(report);
                }
                if (s.Values == null || s.Values.Length != categories.Length)
                {
                    report.AppendLine($"❌ series \"{s.Name}\" has {(s.Values?.Length ?? 0)} value(s) but categories has {categories.Length} entr{(categories.Length == 1 ? "y" : "ies")} — every series' Values length must equal categories.Length exactly.");
                    return Fail(report);
                }
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

                    C.ChartSpace chartSpace = BuildChartSpace(normalizedChartType, categories, series, title, showLegend);

                    string newRelId = PptxCommon.NextRelationshipId(slidePart);
                    ChartPart chartPart = slidePart.AddNewPart<ChartPart>(newRelId);
                    chartPart.ChartSpace = chartSpace;
                    chartPart.ChartSpace.Save();

                    var xfrm = new P.Transform(
                        new A.Offset { X = offsetXEmu, Y = offsetYEmu },
                        new A.Extents { Cx = widthEmu, Cy = heightEmu });

                    P.GraphicFrame? existingFrame = FindChartFrameByName(shapeTree, shapeName);

                    if (existingFrame != null)
                    {
                        string? oldRelId = existingFrame.Graphic?.GraphicData?
                            .GetFirstChild<C.ChartReference>()?.Id?.Value;

                        existingFrame.Transform = xfrm;
                        existingFrame.Graphic = new A.Graphic(
                            new A.GraphicData(new C.ChartReference { Id = newRelId }) { Uri = ChartGraphicDataUri });

                        if (!string.IsNullOrEmpty(oldRelId) && !string.Equals(oldRelId, newRelId, StringComparison.OrdinalIgnoreCase)
                            && slidePart.GetPartById(oldRelId!) is ChartPart oldChartPart)
                        {
                            slidePart.DeletePart(oldChartPart);
                        }

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
                            new A.Graphic(
                                new A.GraphicData(new C.ChartReference { Id = newRelId }) { Uri = ChartGraphicDataUri }));

                        shapeTree.Append(newFrame);
                        created = true;
                    }

                    slidePart.Slide.Save();
                });

                string seriesNote = string.Join(", ", series.Select(s => $"\"{s.Name}\""));
                report.AppendLine(created
                    ? $"✅ Created {normalizedChartType} chart \"{shapeName}\" on slide {slideNumber} — {categories.Length} categor{(categories.Length == 1 ? "y" : "ies")}, {series.Length} series ({seriesNote}), normalized edges top={top:0.####},left={left:0.####},bottom={bottom:0.####},right={right:0.####} -> EMU pos ({offsetXEmu},{offsetYEmu}), size ({widthEmu}x{heightEmu}){(string.IsNullOrWhiteSpace(title) ? "" : $", title \"{title}\"")}."
                    : $"✅ Updated {normalizedChartType} chart \"{shapeName}\" on slide {slideNumber} — {categories.Length} categor{(categories.Length == 1 ? "y" : "ies")}, {series.Length} series ({seriesNote}), normalized edges top={top:0.####},left={left:0.####},bottom={bottom:0.####},right={right:0.####} -> EMU pos ({offsetXEmu},{offsetYEmu}), size ({widthEmu}x{heightEmu}){(string.IsNullOrWhiteSpace(title) ? "" : $", title \"{title}\"")}.");

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

        private static P.GraphicFrame? FindChartFrameByName(P.ShapeTree shapeTree, string name)
        {
            foreach (P.GraphicFrame frame in shapeTree.Elements<P.GraphicFrame>())
            {
                string? n = frame.NonVisualGraphicFrameProperties?.NonVisualDrawingProperties?.Name?.Value;
                if (!string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) continue;

                string? uri = frame.Graphic?.GraphicData?.Uri?.Value;
                if (string.Equals(uri, ChartGraphicDataUri, StringComparison.OrdinalIgnoreCase))
                    return frame;
            }
            return null;
        }

        private static C.ChartSpace BuildChartSpace(
            string chartType, string[] categories, (string Name, double[] Values)[] series,
            string? title, bool showLegend)
        {
            var plotArea = new C.PlotArea();
            plotArea.Append(new C.Layout());

            DocumentFormat.OpenXml.OpenXmlElement chartTypeElement = chartType switch
            {
                "bar" => BuildBarChart(C.BarDirectionValues.Bar, categories, series),
                "column" => BuildBarChart(C.BarDirectionValues.Column, categories, series),
                "line" => BuildLineChart(categories, series),
                _ => throw new InvalidOperationException($"unsupported chart type \"{chartType}\"")
            };
            plotArea.Append(chartTypeElement);
            plotArea.Append(BuildCategoryAxis());
            plotArea.Append(BuildValueAxis());

            var chart = new C.Chart();
            if (!string.IsNullOrWhiteSpace(title))
            {
                chart.Append(BuildTitle(title!));
                chart.Append(new C.AutoTitleDeleted { Val = false });
            }
            else
            {
                chart.Append(new C.AutoTitleDeleted { Val = true });
            }
            chart.Append(plotArea);
            if (showLegend)
                chart.Append(new C.Legend(new C.LegendPosition { Val = C.LegendPositionValues.Bottom }));
            chart.Append(new C.PlotVisibleOnly { Val = true });

            var chartSpace = new C.ChartSpace();
            chartSpace.Append(chart);
            return chartSpace;
        }

        private static C.Title BuildTitle(string text)
        {
            var richText = new C.RichText();
            richText.Append(new A.BodyProperties());
            richText.Append(new A.ListStyle());
            richText.Append(new A.Paragraph(new A.Run(new A.Text(text))));

            var tx = new C.ChartText();
            tx.Append(richText);

            var chartTitle = new C.Title();
            chartTitle.Append(tx);
            chartTitle.Append(new C.Overlay { Val = false });
            return chartTitle;
        }

        private static C.BarChart BuildBarChart(
            C.BarDirectionValues dir, string[] categories, (string Name, double[] Values)[] series)
        {
            var barChart = new C.BarChart();
            barChart.Append(new C.BarDirection { Val = dir });
            barChart.Append(new C.BarGrouping { Val = C.BarGroupingValues.Clustered });
            for (int i = 0; i < series.Length; i++)
                barChart.Append(BuildBarSeries(i, series[i].Name, categories, series[i].Values));
            barChart.Append(new C.GapWidth { Val = 150 });
            barChart.Append(new C.AxisId { Val = CategoryAxisId });
            barChart.Append(new C.AxisId { Val = ValueAxisId });
            return barChart;
        }

        private static C.LineChart BuildLineChart(string[] categories, (string Name, double[] Values)[] series)
        {
            var lineChart = new C.LineChart();
            lineChart.Append(new C.Grouping { Val = C.GroupingValues.Standard });
            for (int i = 0; i < series.Length; i++)
                lineChart.Append(BuildLineSeries(i, series[i].Name, categories, series[i].Values));
            lineChart.Append(new C.AxisId { Val = CategoryAxisId });
            lineChart.Append(new C.AxisId { Val = ValueAxisId });
            return lineChart;
        }

        private static C.BarChartSeries BuildBarSeries(int index, string name, string[] categories, double[] values)
        {
            var ser = new C.BarChartSeries();
            ser.Append(new C.Index { Val = (uint)index });
            ser.Append(new C.Order { Val = (uint)index });
            ser.Append(BuildSeriesText(name));
            ser.Append(new C.CategoryAxisData(BuildStringReference(categories)));
            ser.Append(new C.Values(BuildNumberReference(values)));
            return ser;
        }

        private static C.LineChartSeries BuildLineSeries(int index, string name, string[] categories, double[] values)
        {
            var ser = new C.LineChartSeries();
            ser.Append(new C.Index { Val = (uint)index });
            ser.Append(new C.Order { Val = (uint)index });
            ser.Append(BuildSeriesText(name));
            ser.Append(new C.CategoryAxisData(BuildStringReference(categories)));
            ser.Append(new C.Values(BuildNumberReference(values)));
            ser.Append(new C.Smooth { Val = false });
            return ser;
        }

        private static C.SeriesText BuildSeriesText(string name)
        {
            var tx = new C.SeriesText();
            tx.Append(new C.NumericValue(name));
            return tx;
        }

        private static C.StringReference BuildStringReference(string[] categories)
        {
            var strCache = new C.StringCache();
            strCache.Append(new C.PointCount { Val = (uint)categories.Length });
            for (int i = 0; i < categories.Length; i++)
            {
                var pt = new C.StringPoint { Index = (uint)i };
                pt.Append(new C.NumericValue(categories[i] ?? string.Empty));
                strCache.Append(pt);
            }

            var strRef = new C.StringReference();
            strRef.Append(new C.Formula("Categories"));
            strRef.Append(strCache);
            return strRef;
        }

        private static C.NumberReference BuildNumberReference(double[] values)
        {
            var numCache = new C.NumberingCache();
            numCache.Append(new C.FormatCode("General"));
            numCache.Append(new C.PointCount { Val = (uint)values.Length });
            for (int i = 0; i < values.Length; i++)
            {
                var pt = new C.NumericPoint { Index = (uint)i };
                pt.Append(new C.NumericValue(values[i].ToString(CultureInfo.InvariantCulture)));
                numCache.Append(pt);
            }

            var numRef = new C.NumberReference();
            numRef.Append(new C.Formula("Values"));
            numRef.Append(numCache);
            return numRef;
        }

        private static C.CategoryAxis BuildCategoryAxis()
        {
            var catAx = new C.CategoryAxis();
            catAx.Append(new C.AxisId { Val = CategoryAxisId });
            catAx.Append(new C.Scaling(new C.Orientation { Val = C.OrientationValues.MinMax }));
            catAx.Append(new C.Delete { Val = false });
            catAx.Append(new C.AxisPosition { Val = C.AxisPositionValues.Bottom });
            catAx.Append(new C.MajorTickMark { Val = C.TickMarkValues.Outside });
            catAx.Append(new C.MinorTickMark { Val = C.TickMarkValues.None });
            catAx.Append(new C.TickLabelPosition { Val = C.TickLabelPositionValues.NextTo });
            catAx.Append(new C.CrossingAxis { Val = ValueAxisId });
            catAx.Append(new C.Crosses { Val = C.CrossesValues.AutoZero });
            return catAx;
        }

        private static C.ValueAxis BuildValueAxis()
        {
            var valAx = new C.ValueAxis();
            valAx.Append(new C.AxisId { Val = ValueAxisId });
            valAx.Append(new C.Scaling(new C.Orientation { Val = C.OrientationValues.MinMax }));
            valAx.Append(new C.Delete { Val = false });
            valAx.Append(new C.AxisPosition { Val = C.AxisPositionValues.Left });
            valAx.Append(new C.MajorGridlines());
            valAx.Append(new C.NumberingFormat { FormatCode = "General", SourceLinked = true });
            valAx.Append(new C.MajorTickMark { Val = C.TickMarkValues.Outside });
            valAx.Append(new C.MinorTickMark { Val = C.TickMarkValues.None });
            valAx.Append(new C.TickLabelPosition { Val = C.TickLabelPositionValues.NextTo });
            valAx.Append(new C.CrossingAxis { Val = CategoryAxisId });
            valAx.Append(new C.Crosses { Val = C.CrossesValues.AutoZero });
            return valAx;
        }

        /// <summary>Base64 convenience wrapper — mirrors XlsxModifierTool's *FromBase64 pattern.</summary>
        public static PptxOperationResult SetChartFromBase64(
            string base64,
            int slideNumber,
            string shapeName,
            double top,
            double left,
            double bottom,
            double right,
            string chartType,
            string[] categories,
            (string Name, double[] Values)[] series,
            string? title = null,
            bool showLegend = true)
        {
            if (string.IsNullOrWhiteSpace(base64))
                throw new ArgumentException("base64 must not be null or empty.", nameof(base64));
            return SetChart(Convert.FromBase64String(base64), slideNumber, shapeName,
                top, left, bottom, right, chartType, categories, series, title, showLegend);
        }
    }
}
