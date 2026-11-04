using System;
using System.Collections.Generic;
using System.Linq;
using P = DocumentFormat.OpenXml.Presentation;
using A = DocumentFormat.OpenXml.Drawing;

namespace PptxModifierLib
{
    public static class PptxShapeCommon
    {
        public static bool UpsertBoundedShape(
            P.ShapeTree shapeTree,
            string shapeName,
            long offsetXEmu, long offsetYEmu, long widthEmu, long heightEmu,
            A.ShapeTypeValues preset,
            List<A.ShapeGuide>? adjustValues,
            int? rotationRaw,
            string? normalizedFillColorHex,
            string? text,
            double? fontSizePt = null,
            A.TextAlignmentTypeValues? horizontalAlign = null,
            A.TextAnchoringTypeValues? verticalAlign = null,
            string? normalizedFontColorHex = null)
        {
            P.Shape? existingShape = PptxCommon.FindShapeByName(shapeTree, shapeName);
            bool created;

            if (existingShape != null)
            {
                P.ShapeProperties shapeProps = existingShape.ShapeProperties ??= new P.ShapeProperties();

                int? preservedRotation = rotationRaw ?? shapeProps.Transform2D?.Rotation?.Value;
                var xfrm = new A.Transform2D(
                    new A.Offset { X = offsetXEmu, Y = offsetYEmu },
                    new A.Extents { Cx = widthEmu, Cy = heightEmu })
                {
                    Rotation = preservedRotation
                };
                shapeProps.Transform2D = xfrm;

                if (adjustValues != null)
                    ReplacePresetGeometry(shapeProps, preset, adjustValues);

                if (normalizedFillColorHex != null)
                    ReplaceFill(shapeProps, normalizedFillColorHex);

                if (text != null)
                    SetShapeText(existingShape, text, fontSizePt, horizontalAlign, verticalAlign, normalizedFontColorHex);

                created = false;
            }
            else
            {
                uint newId = PptxCommon.NextShapeId(shapeTree);

                var xfrm = new A.Transform2D(
                    new A.Offset { X = offsetXEmu, Y = offsetYEmu },
                    new A.Extents { Cx = widthEmu, Cy = heightEmu })
                {
                    Rotation = rotationRaw
                };

                var newShape = new P.Shape();
                newShape.NonVisualShapeProperties = new P.NonVisualShapeProperties(
                    new P.NonVisualDrawingProperties { Id = newId, Name = shapeName },
                    new P.NonVisualShapeDrawingProperties(),
                    new P.ApplicationNonVisualDrawingProperties());

                var avList = new A.AdjustValueList();
                if (adjustValues != null)
                    foreach (A.ShapeGuide g in adjustValues)
                        avList.Append((A.ShapeGuide)g.CloneNode(true));

                var shapeProps = new P.ShapeProperties();
                shapeProps.Append(xfrm);
                shapeProps.Append(new A.PresetGeometry(avList) { Preset = preset });
                shapeProps.Append(normalizedFillColorHex != null
                    ? new A.SolidFill(new A.RgbColorModelHex { Val = normalizedFillColorHex })
                    : new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.Accent1 }));
                newShape.ShapeProperties = shapeProps;

                var txBody = new P.TextBody(new A.BodyProperties(), new A.ListStyle());
                newShape.TextBody = txBody;
                ApplyTextAndAutoFit(txBody, text, fontSizePt, horizontalAlign, verticalAlign, normalizedFontColorHex);

                shapeTree.Append(newShape);
                created = true;
            }

            return created;
        }

        public static void ReplaceFill(P.ShapeProperties shapeProps, string hexColor)
        {
            foreach (var existingFill in shapeProps.Elements()
                         .Where(e => e is A.NoFill or A.SolidFill or A.GradientFill
                                       or A.BlipFill or A.PatternFill or A.GroupFill)
                         .ToList())
            {
                existingFill.Remove();
            }

            var newFill = new A.SolidFill(new A.RgbColorModelHex { Val = hexColor });

            DocumentFormat.OpenXml.OpenXmlElement? geometryElement =
                (DocumentFormat.OpenXml.OpenXmlElement?)shapeProps.GetFirstChild<A.PresetGeometry>()
                ?? shapeProps.GetFirstChild<A.CustomGeometry>();

            if (geometryElement != null)
                shapeProps.InsertAfter(newFill, geometryElement);
            else
                shapeProps.Append(newFill);
        }

        public static void ReplacePresetGeometry(P.ShapeProperties shapeProps, A.ShapeTypeValues preset, List<A.ShapeGuide> adjustValues)
        {
            var avList = new A.AdjustValueList();
            foreach (A.ShapeGuide g in adjustValues)
                avList.Append((A.ShapeGuide)g.CloneNode(true));
            var newGeom = new A.PresetGeometry(avList) { Preset = preset };

            var existingGeom = shapeProps.GetFirstChild<A.PresetGeometry>();
            if (existingGeom != null)
            {
                shapeProps.ReplaceChild(newGeom, existingGeom);
            }
            else
            {
                var xfrmEl = shapeProps.GetFirstChild<A.Transform2D>();
                if (xfrmEl != null)
                    shapeProps.InsertAfter(newGeom, xfrmEl);
                else
                    shapeProps.PrependChild(newGeom);
            }
        }

        public static void SetShapeText(
            P.Shape shape, string text, double? fontSizePt = null,
            A.TextAlignmentTypeValues? horizontalAlign = null,
            A.TextAnchoringTypeValues? verticalAlign = null,
            string? fontColorHex = null)
        {
            P.TextBody txBody = shape.TextBody ??= new P.TextBody(new A.BodyProperties(), new A.ListStyle());
            txBody.RemoveAllChildren<A.Paragraph>();

            ApplyTextAndAutoFit(txBody, text, fontSizePt, horizontalAlign, verticalAlign, fontColorHex);
        }

        private static void ApplyTextAndAutoFit(
            P.TextBody txBody, string? text, double? fontSizePt,
            A.TextAlignmentTypeValues? horizontalAlign = null,
            A.TextAnchoringTypeValues? verticalAlign = null,
            string? fontColorHex = null)
        {
            A.BodyProperties bodyProps = txBody.GetFirstChild<A.BodyProperties>()
                ?? throw new InvalidOperationException("TextBody is missing its required a:bodyPr element.");

            foreach (var existingAutoFit in bodyProps.Elements()
                         .Where(e => e is A.NoAutoFit or A.NormalAutoFit or A.ShapeAutoFit)
                         .ToList())
            {
                existingAutoFit.Remove();
            }
            bodyProps.AppendChild(new A.NormalAutoFit());

            if (verticalAlign.HasValue)
                bodyProps.Anchor = verticalAlign.Value;

            var paragraphProps = horizontalAlign.HasValue
                ? new A.ParagraphProperties { Alignment = horizontalAlign.Value }
                : null;
            var paragraph = paragraphProps != null ? new A.Paragraph(paragraphProps) : new A.Paragraph();
            if (!string.IsNullOrEmpty(text))
            {
                A.RunProperties? runProps = (fontSizePt.HasValue || fontColorHex != null)
                    ? new A.RunProperties()
                    : null;
                if (runProps != null && fontSizePt.HasValue)
                    runProps.FontSize = (int)Math.Round(fontSizePt.Value * 100);
                if (runProps != null && fontColorHex != null)
                    runProps.Append(new A.SolidFill(new A.RgbColorModelHex { Val = fontColorHex }));
                paragraph.Append(runProps != null ? new A.Run(runProps, new A.Text(text)) : new A.Run(new A.Text(text)));
            }
            else if (fontSizePt.HasValue || fontColorHex != null)
            {
                var endProps = new A.EndParagraphRunProperties();
                if (fontSizePt.HasValue)
                    endProps.FontSize = (int)Math.Round(fontSizePt.Value * 100);
                if (fontColorHex != null)
                    endProps.Append(new A.SolidFill(new A.RgbColorModelHex { Val = fontColorHex }));
                paragraph.Append(endProps);
            }
            txBody.Append(paragraph);
        }

        public static P.ConnectionShape? FindConnectorByName(P.ShapeTree shapeTree, string name)
        {
            foreach (P.ConnectionShape cxn in shapeTree.Elements<P.ConnectionShape>())
            {
                string? n = cxn.NonVisualConnectionShapeProperties?.NonVisualDrawingProperties?.Name?.Value;
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
                    return cxn;
            }
            return null;
        }

        public readonly struct ConnectorBounds
        {
            public long OffsetXEmu { get; init; }
            public long OffsetYEmu { get; init; }
            public long ExtentsCxEmu { get; init; }
            public long ExtentsCyEmu { get; init; }
            public bool FlipHorizontal { get; init; }
            public bool FlipVertical { get; init; }
        }

        public static ConnectorBounds ComputeConnectorBoundsAndFlip(long startXEmu, long startYEmu, long endXEmu, long endYEmu)
        {
            return new ConnectorBounds
            {
                OffsetXEmu = Math.Min(startXEmu, endXEmu),
                OffsetYEmu = Math.Min(startYEmu, endYEmu),
                ExtentsCxEmu = Math.Abs(endXEmu - startXEmu),
                ExtentsCyEmu = Math.Abs(endYEmu - startYEmu),
                FlipHorizontal = endXEmu < startXEmu,
                FlipVertical = endYEmu < startYEmu
            };
        }

        public static bool UpsertConnector(
            P.ShapeTree shapeTree,
            string shapeName,
            ConnectorBounds bounds,
            DocumentFormat.OpenXml.OpenXmlElement geometryElement,
            A.Outline outline)
        {
            var xfrm = new A.Transform2D(
                new A.Offset { X = bounds.OffsetXEmu, Y = bounds.OffsetYEmu },
                new A.Extents { Cx = bounds.ExtentsCxEmu, Cy = bounds.ExtentsCyEmu })
            {
                HorizontalFlip = bounds.FlipHorizontal,
                VerticalFlip = bounds.FlipVertical
            };

            P.ConnectionShape? existingCxn = FindConnectorByName(shapeTree, shapeName);
            bool created;

            if (existingCxn != null)
            {
                P.ShapeProperties shapeProps = existingCxn.ShapeProperties ??= new P.ShapeProperties();
                shapeProps.Transform2D = xfrm;

                DocumentFormat.OpenXml.OpenXmlElement? existingGeom =
                    (DocumentFormat.OpenXml.OpenXmlElement?)shapeProps.GetFirstChild<A.PresetGeometry>()
                    ?? shapeProps.GetFirstChild<A.CustomGeometry>();
                if (existingGeom != null)
                {
                    shapeProps.ReplaceChild(geometryElement, existingGeom);
                }
                else
                {
                    var xfrmEl = shapeProps.GetFirstChild<A.Transform2D>();
                    if (xfrmEl != null) shapeProps.InsertAfter(geometryElement, xfrmEl);
                    else shapeProps.PrependChild(geometryElement);
                }

                var existingOutline = shapeProps.GetFirstChild<A.Outline>();
                if (existingOutline != null)
                    shapeProps.ReplaceChild(outline, existingOutline);
                else
                    shapeProps.AppendChild(outline);

                created = false;
            }
            else
            {
                uint newId = PptxCommon.NextShapeId(shapeTree);

                var newCxn = new P.ConnectionShape(
                    new P.NonVisualConnectionShapeProperties(
                        new P.NonVisualDrawingProperties { Id = newId, Name = shapeName },
                        new P.NonVisualConnectorShapeDrawingProperties(),
                        new P.ApplicationNonVisualDrawingProperties()),
                    new P.ShapeProperties(
                        xfrm,
                        geometryElement,
                        outline));

                shapeTree.Append(newCxn);
                created = true;
            }

            return created;
        }

        public static A.Outline BuildLineOutline(
            double lineWidthPt,
            string? normalizedLineColorHex,
            A.PresetLineDashValues? dashValue,
            A.LineEndValues? headArrowType,
            A.LineEndValues? tailArrowType)
        {
            var outline = new A.Outline { Width = (int)PptxCommon.PointsToEmu(lineWidthPt) };

            outline.Append(normalizedLineColorHex != null
                ? new A.SolidFill(new A.RgbColorModelHex { Val = normalizedLineColorHex })
                : new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.Text1 }));

            if (dashValue.HasValue)
                outline.Append(new A.PresetDash { Val = dashValue.Value });

            if (headArrowType.HasValue)
                outline.Append(new A.HeadEnd { Type = headArrowType.Value });

            if (tailArrowType.HasValue)
                outline.Append(new A.TailEnd { Type = tailArrowType.Value });

            return outline;
        }

        /// <summary>Maps a friendly dash-style name to its OOXML A.PresetLineDashValues.</summary>
        public static A.PresetLineDashValues? NormalizeDashStyle(string? dashStyle, out string? error)
        {
            error = null;
            switch ((dashStyle ?? "solid").Trim())
            {
                case "solid": return null;
                case "dash": return A.PresetLineDashValues.Dash;
                case "dashDot": return A.PresetLineDashValues.DashDot;
                case "sysDot": return A.PresetLineDashValues.SystemDot;
                case "sysDash": return A.PresetLineDashValues.SystemDash;
                case "lgDash": return A.PresetLineDashValues.LargeDash;
                case "lgDashDot": return A.PresetLineDashValues.LargeDashDot;
                case "lgDashDotDot": return A.PresetLineDashValues.LargeDashDotDot;
                default:
                    error = $"lineDashStyle \"{dashStyle}\" is not recognized — expected \"solid\", \"dash\", \"dashDot\", \"sysDot\", \"sysDash\", \"lgDash\", \"lgDashDot\", or \"lgDashDotDot\".";
                    return null;
            }
        }

        /// <summary>Maps a friendly arrowhead style name to its OOXML A.LineEndValues.</summary>
        public static A.LineEndValues? NormalizeArrowStyle(string? arrowStyle, out string? error)
        {
            error = null;
            switch ((arrowStyle ?? "triangle").Trim())
            {
                case "triangle": return A.LineEndValues.Triangle;
                case "stealth": return A.LineEndValues.Stealth;
                case "oval": return A.LineEndValues.Oval;
                case "diamond": return A.LineEndValues.Diamond;
                case "arrow": return A.LineEndValues.Arrow;
                default:
                    error = $"arrowStyle \"{arrowStyle}\" is not recognized — expected \"triangle\", \"stealth\", \"oval\", \"diamond\", or \"arrow\".";
                    return null;
            }
        }

        /// <summary>Maps a friendly horizontal text-alignment name to OOXML's a:pPr/@algn (A.TextAlignmentTypeValues).</summary>
        public static A.TextAlignmentTypeValues? NormalizeHorizontalAlign(string? horizontalAlign, out string? error)
        {
            error = null;
            if (horizontalAlign == null) return null;
            switch (horizontalAlign.Trim().ToLowerInvariant())
            {
                case "left": return A.TextAlignmentTypeValues.Left;
                case "center": return A.TextAlignmentTypeValues.Center;
                case "right": return A.TextAlignmentTypeValues.Right;
                default:
                    error = $"horizontalAlign \"{horizontalAlign}\" is not recognized — expected \"left\", \"center\", or \"right\".";
                    return null;
            }
        }

        /// <summary>Maps a friendly vertical text-alignment name to OOXML's a:bodyPr/@anchor (A.TextAnchoringTypeValues).</summary>
        public static A.TextAnchoringTypeValues? NormalizeVerticalAlign(string? verticalAlign, out string? error)
        {
            error = null;
            if (verticalAlign == null) return null;
            switch (verticalAlign.Trim().ToLowerInvariant())
            {
                case "top": return A.TextAnchoringTypeValues.Top;
                case "middle": return A.TextAnchoringTypeValues.Center;
                case "bottom": return A.TextAnchoringTypeValues.Bottom;
                default:
                    error = $"verticalAlign \"{verticalAlign}\" is not recognized — expected \"top\", \"middle\", or \"bottom\".";
                    return null;
            }
        }

        public static long RoundnessToAdjustRawValue(double roundness) => (long)Math.Round(roundness * 100000);

        public static int DegreesToOoxmlRotation(double degrees)
        {
            double normalized = degrees % 360.0;
            if (normalized < 0) normalized += 360.0;
            return (int)Math.Round(normalized * 60000.0);
        }

        public static (long X, long Y) ResolveConnectionSitePoint(P.Shape shape, int connectionSiteIndex)
        {
            A.Transform2D? xfrm = shape.ShapeProperties?.Transform2D;
            long x = xfrm?.Offset?.X?.Value ?? 0;
            long y = xfrm?.Offset?.Y?.Value ?? 0;
            long cx = xfrm?.Extents?.Cx?.Value ?? 0;
            long cy = xfrm?.Extents?.Cy?.Value ?? 0;
            long centerX = x + cx / 2;
            long centerY = y + cy / 2;

            return connectionSiteIndex switch
            {
                0 => (centerX, y),      // top
                1 => (x + cx, centerY), // right
                2 => (centerX, y + cy), // bottom
                3 => (x, centerY),      // left
                _ => throw new ArgumentOutOfRangeException(nameof(connectionSiteIndex),
                    "connectionSiteIndex must be 0 (top), 1 (right), 2 (bottom), or 3 (left).")
            };
        }

        public static (A.CustomGeometry Geometry, ConnectorBounds Bounds) BuildMultiPointPathGeometry(
            IReadOnlyList<(long X, long Y)> absolutePoints)
        {
            if (absolutePoints == null || absolutePoints.Count < 2)
                throw new ArgumentException("absolutePoints must contain at least 2 points.", nameof(absolutePoints));

            long minX = absolutePoints[0].X, maxX = absolutePoints[0].X;
            long minY = absolutePoints[0].Y, maxY = absolutePoints[0].Y;
            foreach (var p in absolutePoints)
            {
                if (p.X < minX) minX = p.X;
                if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.Y > maxY) maxY = p.Y;
            }

            // a:path/@w and @h must both be > 0 — guard against a fully
            // horizontal or fully vertical path collapsing one dimension to 0.
            long pathWidth = Math.Max(1, maxX - minX);
            long pathHeight = Math.Max(1, maxY - minY);

            var path = new A.Path { Width = pathWidth, Height = pathHeight };
            for (int i = 0; i < absolutePoints.Count; i++)
            {
                long localX = absolutePoints[i].X - minX;
                long localY = absolutePoints[i].Y - minY;
                path.Append(i == 0
                    ? new A.MoveTo(new A.Point { X = localX.ToString(), Y = localY.ToString() })
                    : new A.LineTo(new A.Point { X = localX.ToString(), Y = localY.ToString() }));
            }

            var custGeom = new A.CustomGeometry();
            custGeom.Append(new A.PathList(path));

            var bounds = new ConnectorBounds
            {
                OffsetXEmu = minX,
                OffsetYEmu = minY,
                ExtentsCxEmu = maxX - minX,
                ExtentsCyEmu = maxY - minY,
                FlipHorizontal = false,
                FlipVertical = false
            };

            return (custGeom, bounds);
        }

        public static bool UpsertCustomPathShape(
            P.ShapeTree shapeTree,
            string shapeName,
            ConnectorBounds bounds,
            A.CustomGeometry geometry,
            A.Outline outline)
        {
            var xfrm = new A.Transform2D(
                new A.Offset { X = bounds.OffsetXEmu, Y = bounds.OffsetYEmu },
                new A.Extents { Cx = bounds.ExtentsCxEmu, Cy = bounds.ExtentsCyEmu })
            {
                HorizontalFlip = bounds.FlipHorizontal,
                VerticalFlip = bounds.FlipVertical
            };

            P.Shape? existingShape = PptxCommon.FindShapeByName(shapeTree, shapeName);
            bool created;

            if (existingShape != null)
            {
                P.ShapeProperties shapeProps = existingShape.ShapeProperties ??= new P.ShapeProperties();
                shapeProps.Transform2D = xfrm;

                DocumentFormat.OpenXml.OpenXmlElement? existingGeom =
                    (DocumentFormat.OpenXml.OpenXmlElement?)shapeProps.GetFirstChild<A.PresetGeometry>()
                    ?? shapeProps.GetFirstChild<A.CustomGeometry>();
                if (existingGeom != null)
                    shapeProps.ReplaceChild(geometry, existingGeom);
                else
                {
                    var xfrmEl = shapeProps.GetFirstChild<A.Transform2D>();
                    if (xfrmEl != null) shapeProps.InsertAfter(geometry, xfrmEl);
                    else shapeProps.PrependChild(geometry);
                }

                // Always force NoFill — this is a line-only path, never filled,
                // regardless of what fill (if any) a same-named shape had before.
                foreach (var existingFill in shapeProps.Elements()
                             .Where(e => e is A.NoFill or A.SolidFill or A.GradientFill
                                           or A.BlipFill or A.PatternFill or A.GroupFill)
                             .ToList())
                {
                    existingFill.Remove();
                }
                DocumentFormat.OpenXml.OpenXmlElement geomForFillAnchor =
                    (DocumentFormat.OpenXml.OpenXmlElement?)shapeProps.GetFirstChild<A.CustomGeometry>()
                    ?? shapeProps.GetFirstChild<A.PresetGeometry>()!;
                shapeProps.InsertAfter(new A.NoFill(), geomForFillAnchor);

                var existingOutline = shapeProps.GetFirstChild<A.Outline>();
                if (existingOutline != null)
                    shapeProps.ReplaceChild(outline, existingOutline);
                else
                    shapeProps.AppendChild(outline);

                created = false;
            }
            else
            {
                uint newId = PptxCommon.NextShapeId(shapeTree);

                var newShape = new P.Shape();
                newShape.NonVisualShapeProperties = new P.NonVisualShapeProperties(
                    new P.NonVisualDrawingProperties { Id = newId, Name = shapeName },
                    new P.NonVisualShapeDrawingProperties(),
                    new P.ApplicationNonVisualDrawingProperties());

                var shapeProps = new P.ShapeProperties();
                shapeProps.Append(xfrm);
                shapeProps.Append(geometry);
                shapeProps.Append(new A.NoFill());
                shapeProps.Append(outline);
                newShape.ShapeProperties = shapeProps;

                var txBody = new P.TextBody(new A.BodyProperties(), new A.ListStyle());
                txBody.Append(new A.Paragraph());
                newShape.TextBody = txBody;

                shapeTree.Append(newShape);
                created = true;
            }

            return created;
        }
    }
}
