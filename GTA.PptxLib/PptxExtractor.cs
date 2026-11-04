using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using Microsoft.Office.Core;
using Microsoft.Office.Interop.PowerPoint;

namespace GTA.PptxLib
{

    public sealed class SlideResult
    {
        public int    SlideNumber { get; init; }

        public byte[] PngBytes   { get; init; } = Array.Empty<byte>();

        public string XmlContent { get; init; } = string.Empty;

        public string PngBase64  => Convert.ToBase64String(PngBytes);
    }

    public class PptxExtractor
    {

        public static List<SlideResult> ExtractFromBytes(byte[] pptxBytes,
                                                         int    width  = 1920,
                                                         int    height = 1080)
        {
            if (pptxBytes == null || pptxBytes.Length == 0)
                throw new ArgumentException("pptxBytes must not be null or empty.", nameof(pptxBytes));

            return ExtractCore(pptxBytes, width, height);
        }

        public static List<SlideResult> ExtractFromBase64(string base64,
                                                          int    width  = 1920,
                                                          int    height = 1080)
        {
            if (string.IsNullOrWhiteSpace(base64))
                throw new ArgumentException("base64 must not be null or empty.", nameof(base64));

            byte[] bytes = Convert.FromBase64String(base64);
            return ExtractCore(bytes, width, height);
        }

        public static List<SlideResult> ExtractFromStream(Stream stream,
                                                          int    width  = 1920,
                                                          int    height = 1080)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));

            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ExtractCore(ms.ToArray(), width, height);
        }

        public static void Run(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("Usage: GTA.PptxLib.dll <pptx_path> [output_dir] [width] [height]");
                Console.WriteLine();
                Console.WriteLine("  pptx_path  : Full absolute path to the .pptx file  (REQUIRED)");
                Console.WriteLine("  output_dir : Output folder path                     (optional)");
                Console.WriteLine("               Default: <same folder as pptx>\\<name>_extracted");
                Console.WriteLine("  width      : PNG export width  in pixels            (optional, default 1920)");
                Console.WriteLine("  height     : PNG export height in pixels            (optional, default 1080)");
                Console.WriteLine();
                Console.WriteLine("Output per slide:");
                Console.WriteLine("  slide_001.png  — rendered image of the slide");
                Console.WriteLine("  slide_001.xml  — raw OpenXML of the slide (pretty-printed)");
                return;
            }

            string pptxPath = args[0].Trim('"', '\'');

            if (!File.Exists(pptxPath))
            {
                Console.WriteLine($"[ERROR] File not found: {pptxPath}");
                return;
            }

            string defaultOutputDir = Path.Combine(
                Path.GetDirectoryName(pptxPath)!,
                Path.GetFileNameWithoutExtension(pptxPath) + "_extracted"
            );

            string outputDir = args.Length > 1 ? args[1].Trim('"', '\'') : defaultOutputDir;
            int    width     = args.Length > 2 && int.TryParse(args[2], out int w) ? w : 1920;
            int    height    = args.Length > 3 && int.TryParse(args[3], out int h) ? h : 1080;

            Directory.CreateDirectory(outputDir);

            Console.WriteLine($"Source  : {pptxPath}");
            Console.WriteLine($"Output  : {outputDir}");
            Console.WriteLine($"PNG size: {width} x {height}");

            Console.WriteLine("\n[Step 1] Exporting slides as PNG via PowerPoint COM...");
            Console.WriteLine("\n[Step 2] Extracting slide XML from PPTX ZIP...");

            byte[]            pptxBytes = File.ReadAllBytes(pptxPath);
            List<SlideResult> results   = ExtractCore(pptxBytes, width, height);

            foreach (SlideResult slide in results)
            {
                string pngPath = Path.Combine(outputDir, $"slide_{slide.SlideNumber:D3}.png");
                string xmlPath = Path.Combine(outputDir, $"slide_{slide.SlideNumber:D3}.xml");

                File.WriteAllBytes(pngPath, slide.PngBytes);
                File.WriteAllText(xmlPath, slide.XmlContent);

                Console.WriteLine($"  [PNG] slide_{slide.SlideNumber:D3}.png");
                Console.WriteLine($"  [XML] slide_{slide.SlideNumber:D3}.xml");
            }

            Console.WriteLine("\n[DONE] All slides extracted successfully.");
        }

        private static List<SlideResult> ExtractCore(byte[] pptxBytes, int width, int height)
        {
            Dictionary<int, string> xmlMap = ExtractXmlFromBytes(pptxBytes);
            Dictionary<int, byte[]> pngMap = ExportPngFromBytes(pptxBytes, width, height);

            IEnumerable<int> slideNumbers = xmlMap.Keys.Union(pngMap.Keys).OrderBy(n => n);

            return slideNumbers.Select(n => new SlideResult
            {
                SlideNumber = n,
                PngBytes    = pngMap.TryGetValue(n, out byte[]? png) ? png : Array.Empty<byte>(),
                XmlContent  = xmlMap.TryGetValue(n, out string? xml) ? xml  : string.Empty,
            }).ToList();
        }

        private static Dictionary<int, string> ExtractXmlFromBytes(byte[] pptxBytes)
        {
            var result = new Dictionary<int, string>();

            using var ms  = new MemoryStream(pptxBytes, writable: false);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: false);

            var slideEntries = zip.Entries
                .Where(e =>
                    e.FullName.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase) &&
                    e.FullName.EndsWith(".xml",               StringComparison.OrdinalIgnoreCase))
                .OrderBy(e =>
                {
                    string numStr = Path.GetFileNameWithoutExtension(e.Name).Replace("slide", "");
                    return int.TryParse(numStr, out int n) ? n : 9999;
                });

            foreach (ZipArchiveEntry entry in slideEntries)
            {
                string numStr = Path.GetFileNameWithoutExtension(entry.Name).Replace("slide", "");
                if (!int.TryParse(numStr, out int slideNum)) continue;

                using Stream  stream = entry.Open();
                XDocument     xdoc   = XDocument.Load(stream);

                using var sw = new StringWriter();
                xdoc.Save(sw);
                result[slideNum] = sw.ToString();
            }

            return result;
        }

        private static Dictionary<int, byte[]> ExportPngFromBytes(byte[] pptxBytes,
                                                                   int    width,
                                                                   int    height)
        {
            string sessionId  = Guid.NewGuid().ToString("N");
            string tempPptx   = Path.Combine(Path.GetTempPath(), $"pptx_{sessionId}.pptx");
            string tempPngDir = Path.Combine(Path.GetTempPath(), $"pptx_{sessionId}_pngs");

            try
            {
                File.WriteAllBytes(tempPptx, pptxBytes);
                Directory.CreateDirectory(tempPngDir);

                ExportSlidesAsPng(tempPptx, tempPngDir, width, height);

                var result = new Dictionary<int, byte[]>();

                foreach (string pngFile in Directory.GetFiles(tempPngDir, "slide_*.png")
                                                     .OrderBy(f => f))
                {
                    string numStr = Path.GetFileNameWithoutExtension(pngFile)
                                        .Replace("slide_", "", StringComparison.OrdinalIgnoreCase);
                    if (int.TryParse(numStr, out int slideNum))
                        result[slideNum] = File.ReadAllBytes(pngFile);
                }

                return result;
            }
            finally
            {
                try { File.Delete(tempPptx); }                        catch { /* best-effort */ }
                try { Directory.Delete(tempPngDir, recursive: true); } catch { /* best-effort */ }
            }
        }

        private static void ExportSlidesAsPng(string pptxPath, string outputDir,
                                              int width, int height)
        {
            Microsoft.Office.Interop.PowerPoint.Application? app  = null;
            Presentation?                                     pres = null;

            try
            {
                app = new Microsoft.Office.Interop.PowerPoint.Application();

                pres = app.Presentations.Open(
                    pptxPath,
                    MsoTriState.msoTrue,   // ReadOnly
                    MsoTriState.msoFalse,  // Untitled
                    MsoTriState.msoFalse   // WithWindow
                );

                int slideCount = pres.Slides.Count;

                for (int i = 1; i <= slideCount; i++)
                {
                    Slide  slide   = pres.Slides[i];
                    string pngPath = Path.Combine(outputDir, $"slide_{i:D3}.png");
                    slide.Export(pngPath, "PNG", width, height);
                }
            }
            finally
            {
                try { pres?.Close(); } catch { }
                try { app?.Quit();   } catch { }
                if (pres != null) Marshal.ReleaseComObject(pres);
                if (app  != null) Marshal.ReleaseComObject(app);
            }
        }
    }
}
