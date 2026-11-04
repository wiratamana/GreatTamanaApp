using System;
using DocumentFormat.OpenXml.Packaging;
using P = DocumentFormat.OpenXml.Presentation;

namespace PptxModifierLib
{
    public static class PptxNormalizedCoordinates
    {
        public static (long WidthEmu, long HeightEmu) GetSlideSizeEmu(PresentationPart presentationPart)
        {
            P.SlideSize? slideSize = presentationPart.Presentation?.SlideSize;
            long width = slideSize?.Cx?.Value ?? PptxCommon.WidescreenWidthEmu;
            long height = slideSize?.Cy?.Value ?? PptxCommon.WidescreenHeightEmu;
            return (width, height);
        }

        public static (long X, long Y) PointToEmu(
            double normalizedX, double normalizedY, long slideWidthEmu, long slideHeightEmu)
        {
            return (
                (long)Math.Round(normalizedX * slideWidthEmu),
                (long)Math.Round(normalizedY * slideHeightEmu));
        }

        public static (long OffsetXEmu, long OffsetYEmu, long WidthEmu, long HeightEmu) RectFromEdgesToEmu(
            double normalizedTop, double normalizedLeft, double normalizedBottom, double normalizedRight,
            long slideWidthEmu, long slideHeightEmu)
        {
            long leftEmu = (long)Math.Round(normalizedLeft * slideWidthEmu);
            long topEmu = (long)Math.Round(normalizedTop * slideHeightEmu);
            long rightEmu = (long)Math.Round(normalizedRight * slideWidthEmu);
            long bottomEmu = (long)Math.Round(normalizedBottom * slideHeightEmu);
            return (leftEmu, topEmu, rightEmu - leftEmu, bottomEmu - topEmu);
        }

        /// <summary>Converts a normalized length along the slide width to EMU.</summary>
        public static long LengthToEmuByWidth(double normalizedLength, long slideWidthEmu)
            => (long)Math.Round(normalizedLength * slideWidthEmu);

        public static long LengthToEmuByHeight(double normalizedLength, long slideHeightEmu)
            => (long)Math.Round(normalizedLength * slideHeightEmu);
    }
}
