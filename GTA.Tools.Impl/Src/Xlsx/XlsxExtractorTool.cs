using System;
using System.Collections.Generic;
using System.IO;

namespace GTA.XlsxLib
{

    /// <summary>A single embedded image extracted from an xlsx workbook.</summary>
    public sealed class XlsxImageResult
    {
        /// <summary>Worksheet name the image belongs to.</summary>
        public string SheetName { get; init; } = string.Empty;

        /// <summary>Safe filename (with extension) derived from sheet name + cell + picture name.</summary>
        public string Name      { get; init; } = string.Empty;

        /// <summary>Cell address of the image's top-left anchor cell.</summary>
        public string CellAddress { get; init; } = string.Empty;

        /// <summary>MIME type: "image/png", "image/jpeg", "image/gif", "image/bmp", etc.</summary>
        public string MediaType { get; init; } = "image/png";

        /// <summary>Raw image bytes.</summary>
        public byte[] Bytes     { get; init; } = Array.Empty<byte>();

        /// <summary>Convenience: Base64-encoded image, ready for a JSON/data-URI payload.</summary>
        public string Base64    => Convert.ToBase64String(Bytes);
    }

    /// <summary>Full extraction result for one xlsx workbook, held entirely in memory.</summary>
    public sealed class XlsxExtractionResult
    {
        public string XmlContent             { get; init; } = string.Empty;

        /// <summary>All embedded images extracted from every sheet.</summary>
        public List<XlsxImageResult> Images  { get; init; } = new List<XlsxImageResult>();
    }

    public partial class XlsxExtractorTool
    {
        public static void Run(string[] args)
        {
            if (args == null || args.Length == 0) { PrintUsage(); return; }
            Run(args[0]);
        }

        public static void Run(string xlsxPath)
        {
            string fullPath = Path.GetFullPath(xlsxPath);
            if (!File.Exists(fullPath))
            {
                Console.WriteLine($"ERROR: File not found: {fullPath}");
                return;
            }

            string fileNameNoExt = Path.GetFileNameWithoutExtension(fullPath);
            string directory     = Path.GetDirectoryName(fullPath) ?? "";
            string outputFolder  = Path.Combine(directory, $"{fileNameNoExt}_extracted");
            Directory.CreateDirectory(outputFolder);
            string outputXmlPath = Path.Combine(outputFolder, $"{fileNameNoExt}_LLM.xml");

            Console.WriteLine("===============================================================");
            Console.WriteLine("  GTA.XlsxLib v3 -- Starting Extraction");
            Console.WriteLine("===============================================================");
            Console.WriteLine($"  Input  : {fullPath}");
            Console.WriteLine($"  Output : {outputFolder}");
            Console.WriteLine("---------------------------------------------------------------\n");

            Console.WriteLine("[Step 1] Extracting embedded images via ClosedXML...");
            int imageCount = ExtractImagesWithClosedXml(fullPath, outputFolder);
            Console.WriteLine("[Step 2] Pre-extracting metadata (merges, formatting, heights, pictures) via ClosedXML...");
            byte[] xlsxBytesForPre = File.ReadAllBytes(fullPath);
            var precomputed = PreExtractWithClosedXml(xlsxBytesForPre);
            Console.WriteLine($"  -> Pre-extraction ready for {precomputed.Count} sheet(s).\n");

            Console.WriteLine("[Step 3] Extracting cell values, column widths, and text shapes via Excel Interop...");
            ExtractDataWithInterop(fullPath, outputXmlPath, precomputed);

            Console.WriteLine("\n===============================================================");
            Console.WriteLine($"Done! Output folder: {outputFolder}");
            Console.WriteLine("===============================================================");
        }

        /// <summary>Extract all worksheet data and embedded images from raw XLSX bytes.</summary>
        public static XlsxExtractionResult ExtractFromBytes(byte[] xlsxBytes)
        {
            if (xlsxBytes == null || xlsxBytes.Length == 0)
                throw new ArgumentException("xlsxBytes must not be null or empty.", nameof(xlsxBytes));
            return ExtractCore(xlsxBytes);
        }

        /// <summary>Extract from a Base64-encoded XLSX string.</summary>
        public static XlsxExtractionResult ExtractFromBase64(string base64)
        {
            if (string.IsNullOrWhiteSpace(base64))
                throw new ArgumentException("base64 must not be null or empty.", nameof(base64));
            return ExtractCore(Convert.FromBase64String(base64));
        }

        /// <summary>Extract from any readable Stream.</summary>
        public static XlsxExtractionResult ExtractFromStream(Stream stream)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ExtractCore(ms.ToArray());
        }


        /// <summary>Extract ONLY the workbook XML (cell data, formatting, layout, merges, shape positions, image positions).</summary>
        public static string ExtractXmlOnlyFromBase64(string base64)
        {
            if (string.IsNullOrWhiteSpace(base64))
                throw new ArgumentException("base64 must not be null or empty.", nameof(base64));
            return ExtractXmlFromBytes(Convert.FromBase64String(base64));
        }

        /// <summary>Extract ONLY the embedded images (via ClosedXML).</summary>
        public static List<XlsxImageResult> ExtractImagesOnlyFromBase64(string base64)
        {
            if (string.IsNullOrWhiteSpace(base64))
                throw new ArgumentException("base64 must not be null or empty.", nameof(base64));
            byte[] bytes = Convert.FromBase64String(base64);
            using var ms = new MemoryStream(bytes, writable: false);
            return ExtractImagesFromStream(ms);
        }

        private static XlsxExtractionResult ExtractCore(byte[] xlsxBytes)
        {
            // Images: ClosedXML can read directly from a MemoryStream — no file needed
            List<XlsxImageResult> images;
            using (var ms = new MemoryStream(xlsxBytes, writable: false))
                images = ExtractImagesFromStream(ms);

            // XML: Excel COM requires a temp file internally (hidden from caller)
            string xmlContent = ExtractXmlFromBytes(xlsxBytes);

            return new XlsxExtractionResult
            {
                XmlContent = xmlContent,
                Images     = images
            };
        }

        private static void PrintUsage()
        {
            Console.WriteLine("================================================================");
            Console.WriteLine("  GTA.XlsxLib v3 -- Usage");
            Console.WriteLine("================================================================");
            Console.WriteLine("  Extracts xlsx to a folder with XML + image files.");
            Console.WriteLine("  Captures: cells, formatting, column widths, row heights,");
            Console.WriteLine("            merged cells, shape positions, image positions.");
            Console.WriteLine("  All sizes in EMU (1 point = 12700 EMU).");
            Console.WriteLine("");
            Console.WriteLine("  args[0] = full path to the .xlsx file");
            Console.WriteLine("================================================================");
        }
    }
}
