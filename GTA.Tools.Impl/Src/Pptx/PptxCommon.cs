using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using P = DocumentFormat.OpenXml.Presentation;

namespace PptxModifierLib
{
    public sealed class PptxOperationResult
    {
        public bool    Success       { get; init; }
        public byte[]? ModifiedBytes { get; init; }
        public string  ReportText    { get; init; } = string.Empty;
    }

    public static class PptxCommon
    {
        public const long EmuPerInch = 914400;
        public const long EmuPerCm   = 360000;
        public const long EmuPerPoint = 12700;

        // Standard PowerPoint slide sizes, in EMU.
        public const long WidescreenWidthEmu  = 12192000; // 16:9 — 13.333in
        public const long WidescreenHeightEmu = 6858000;  // 16:9 — 7.5in
        public const long StandardWidthEmu    = 9144000;  // 4:3  — 10in
        public const long StandardHeightEmu   = 6858000;  // 4:3  — 7.5in

        public static long InchesToEmu(double inches)        => (long)Math.Round(inches * EmuPerInch);
        public static long CmToEmu(double centimeters)       => (long)Math.Round(centimeters * EmuPerCm);
        public static long PointsToEmu(double points)        => (long)Math.Round(points * EmuPerPoint);
        public static double EmuToInches(long emu)           => emu / (double)EmuPerInch;
        public static double EmuToCm(long emu)                => emu / (double)EmuPerCm;
        public static double EmuToPoints(long emu)            => emu / (double)EmuPerPoint;

        public static byte[] EditInMemory(byte[] pptxBytes, Action<PresentationDocument> mutate)
        {
            if (pptxBytes == null || pptxBytes.Length == 0)
                throw new ArgumentException("pptxBytes must not be null or empty.", nameof(pptxBytes));
            if (mutate == null)
                throw new ArgumentNullException(nameof(mutate));

            using var ms = new MemoryStream();
            ms.Write(pptxBytes, 0, pptxBytes.Length);
            ms.Position = 0;

            using (PresentationDocument doc = PresentationDocument.Open(ms, true))
            {
                mutate(doc);
                doc.Save();
            }
            return ms.ToArray();
        }

        public static string? NormalizeHexColor(string? hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return null;

            string v = hex.Trim().TrimStart('#');
            if (v.Length == 3)
                v = string.Concat(v[0], v[0], v[1], v[1], v[2], v[2]);

            if (v.Length != 6 || !Regex.IsMatch(v, "^[0-9A-Fa-f]{6}$"))
                return null;

            return v.ToUpperInvariant();
        }

        public static List<SlidePart> GetOrderedSlideParts(PresentationPart presentationPart)
        {
            var result = new List<SlidePart>();
            P.SlideIdList? slideIdList = presentationPart.Presentation?.SlideIdList;
            if (slideIdList == null) return result;

            foreach (P.SlideId slideId in slideIdList.Elements<P.SlideId>())
            {
                string? relId = slideId.RelationshipId?.Value;
                if (string.IsNullOrEmpty(relId)) continue;

                if (presentationPart.GetPartById(relId!) is SlidePart sp)
                    result.Add(sp);
            }
            return result;
        }

        public static uint NextShapeId(P.ShapeTree shapeTree)
        {
            uint max = 1;
            foreach (P.NonVisualDrawingProperties nv in shapeTree.Descendants<P.NonVisualDrawingProperties>())
            {
                if (nv.Id != null && nv.Id.Value > max)
                    max = nv.Id.Value;
            }
            return max + 1;
        }

        public static P.Shape? FindShapeByName(P.ShapeTree shapeTree, string name)
        {
            foreach (P.Shape shape in shapeTree.Elements<P.Shape>())
            {
                string? n = shape.NonVisualShapeProperties?.NonVisualDrawingProperties?.Name?.Value;
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
                    return shape;
            }
            return null;
        }

        public static string NextRelationshipId(OpenXmlPartContainer container)
        {
            int max = 0;
            foreach (IdPartPair pair in container.Parts)
            {
                if (pair.RelationshipId != null &&
                    pair.RelationshipId.StartsWith("rId", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(pair.RelationshipId.AsSpan(3), out int n) && n > max)
                {
                    max = n;
                }
            }
            return $"rId{max + 1}";
        }
    }
}
