using System;
using System.Collections.Generic;
using System.IO;
using PDFtoImage;
using SkiaSharp;

namespace GTA.PdfLib
{

    public sealed class PdfPageResult
    {
        /// <summary>1-based page number.</summary>
        public int PageNumber { get; init; }

        public byte[] PngBytes { get; init; } = Array.Empty<byte>();

        /// <summary>Convenience: Base64-encoded PNG, ready for a data-URI or JSON payload.</summary>
        public string PngBase64 => Convert.ToBase64String(PngBytes);
    }

    public class PdfExtractor
    {

        public static List<PdfPageResult> ExtractFromBytes(byte[] pdfBytes, int dpi = 150)
        {
            if (pdfBytes == null || pdfBytes.Length == 0)
                throw new ArgumentException("pdfBytes must not be null or empty.", nameof(pdfBytes));

            return ExtractCore(pdfBytes, dpi);
        }

        public static List<PdfPageResult> ExtractFromBase64(string base64, int dpi = 150)
        {
            if (string.IsNullOrWhiteSpace(base64))
                throw new ArgumentException("base64 must not be null or empty.", nameof(base64));

            byte[] bytes = Convert.FromBase64String(base64);
            return ExtractCore(bytes, dpi);
        }

        public static List<PdfPageResult> ExtractFromStream(Stream stream, int dpi = 150)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));

            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ExtractCore(ms.ToArray(), dpi);
        }

        /// <summary>Returns the page count of a PDF without rendering any page.</summary>
        public static int GetPageCount(byte[] pdfBytes) => Conversion.GetPageCount(pdfBytes);

        /// <summary>Returns the page count of a Base64-encoded PDF without rendering any page.</summary>
        public static int GetPageCountFromBase64(string base64) => Conversion.GetPageCount(Convert.FromBase64String(base64));

        private static List<PdfPageResult> ExtractCore(byte[] pdfBytes, int dpi)
        {
            var options = new RenderOptions(Dpi: dpi);
            var results = new List<PdfPageResult>();

            int pageNum = 1;

            foreach (SKBitmap bitmap in Conversion.ToImages(pdfBytes, password: null, options: options))
            {
                using (bitmap)
                using (SKData png = bitmap.Encode(SKEncodedImageFormat.Png, 100))
                {
                    results.Add(new PdfPageResult
                    {
                        PageNumber = pageNum++,
                        PngBytes   = png.ToArray(),
                    });
                }
            }

            return results;
        }
    }
}
