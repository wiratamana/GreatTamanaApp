using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Vml = DocumentFormat.OpenXml.Vml;
using DrawingShape = DocumentFormat.OpenXml.Office2010.Word.DrawingShape;
using Drawing = DocumentFormat.OpenXml.Drawing;

namespace GTA.DocxLib
{

    public class DocxExtractionResult
    {
        public List<string>     Paragraphs  { get; set; } = new List<string>();
        public List<TableData>  Tables      { get; set; } = new List<TableData>();
        public List<ShapeData>  Shapes      { get; set; } = new List<ShapeData>();
        public List<ImageData>  Images      { get; set; } = new List<ImageData>();

        public string XmlContent { get; set; } = string.Empty;
    }

    public class TableData
    {
        public int                RowCount => Rows.Count;
        public List<List<string>> Rows     { get; set; } = new List<List<string>>();
    }

    public class ShapeData
    {
        public string Id         { get; set; } = "";
        public string Name       { get; set; } = "";
        public string TextInside { get; set; } = "";
    }

    public class ImageData
    {
        public string FileName    { get; set; } = "";
        public string ContentType { get; set; } = "";
        public byte[] Bytes       { get; set; } = Array.Empty<byte>();
    }

    public class DocxExtractorTool
    {
        public static void Run(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            if (args == null || args.Length == 0)
            {
                PrintUsage();
                return;
            }

            Run(args[0]);
        }

        public static void Run(string docxPath)
        {
            string fullPath = Path.GetFullPath(docxPath);
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
            Console.WriteLine("  GTA.DocxLib v2 -- Starting Extraction");
            Console.WriteLine("===============================================================");
            Console.WriteLine($"  Input  : {fullPath}");
            Console.WriteLine($"  Output : {outputFolder}");
            Console.WriteLine("---------------------------------------------------------------\n");

            DocxExtractionResult result = Extract(fullPath);

            Console.WriteLine($"[Step 1] Saving {result.Images.Count} image(s)...");
            foreach (ImageData image in result.Images)
            {
                string savePath = Path.Combine(outputFolder, image.FileName);
                File.WriteAllBytes(savePath, image.Bytes);
                Console.WriteLine($"  [Image] Saved: {image.FileName}  ({image.Bytes.Length:N0} bytes, {image.ContentType})");
            }

            Console.WriteLine($"\n[Step 2] Saving XML ({result.Paragraphs.Count} paragraphs, " +
                              $"{result.Tables.Count} tables, {result.Shapes.Count} shapes)...");
            File.WriteAllText(outputXmlPath, result.XmlContent, System.Text.Encoding.UTF8);
            Console.WriteLine($"  [XML]   Saved: {Path.GetFileName(outputXmlPath)}");

            Console.WriteLine("\n===============================================================");
            Console.WriteLine($"Done! Output folder: {outputFolder}");
            Console.WriteLine("===============================================================");
        }

        public static DocxExtractionResult Extract(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("The specified file was not found.", filePath);

            using (var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                string fileNameNoExt = Path.GetFileNameWithoutExtension(filePath);
                return Extract(stream, fileNameNoExt);
            }
        }

        public static DocxExtractionResult Extract(Stream docxStream, string documentName = "Document")
        {
            var result = new DocxExtractionResult();

            using (WordprocessingDocument wordDoc = WordprocessingDocument.Open(docxStream, false))
            {
                var mainPart = wordDoc.MainDocumentPart;
                if (mainPart == null) return result;

                var body = mainPart.Document.Body;
                if (body == null) return result;

                var paragraphs = body.ChildElements.OfType<Paragraph>();
                foreach (var p in paragraphs)
                {
                    if (!string.IsNullOrWhiteSpace(p.InnerText))
                        result.Paragraphs.Add(p.InnerText);
                }

                var tables = body.Descendants<Table>();
                foreach (var table in tables)
                {
                    var tableData = new TableData();
                    foreach (var row in table.Descendants<TableRow>())
                    {
                        var rowCells = new List<string>();
                        foreach (var cell in row.Descendants<TableCell>())
                            rowCells.Add(cell.InnerText ?? "");
                        tableData.Rows.Add(rowCells);
                    }
                    result.Tables.Add(tableData);
                }

                var vmlShapes = body.Descendants<Vml.Shape>();
                foreach (var vmlShape in vmlShapes)
                {
                    var textBoxContent = vmlShape.Descendants<TextBoxContent>().FirstOrDefault();
                    string textInside  = textBoxContent != null ? textBoxContent.InnerText : "";

                    result.Shapes.Add(new ShapeData
                    {
                        Id         = vmlShape.Id?.Value ?? "N/A",
                        Name       = vmlShape.Title?.Value ?? "VmlShape",
                        TextInside = textInside
                    });
                }

                var drawingShapes = body.Descendants<DrawingShape.WordprocessingShape>();
                foreach (var dmlShape in drawingShapes)
                {
                    var textBody  = dmlShape.Descendants<Drawing.TextBody>().FirstOrDefault();
                    string textInside = textBody != null ? textBody.InnerText : "";

                    var nvProps = dmlShape.NonVisualDrawingProperties;
                    result.Shapes.Add(new ShapeData
                    {
                        Id         = nvProps?.Id?.Value.ToString() ?? "N/A",
                        Name       = nvProps?.Name?.Value ?? "DrawingMLShape",
                        TextInside = textInside
                    });
                }

                foreach (var imagePart in mainPart.ImageParts)
                {
                    string uri         = imagePart.Uri.ToString();
                    string fileName    = Path.GetFileName(uri);
                    string contentType = imagePart.ContentType;

                    byte[] imageBytes;
                    using (var imageStream = imagePart.GetStream())
                    using (var ms = new MemoryStream())
                    {
                        imageStream.CopyTo(ms);
                        imageBytes = ms.ToArray();
                    }

                    result.Images.Add(new ImageData
                    {
                        FileName    = fileName,
                        ContentType = contentType,
                        Bytes       = imageBytes
                    });
                }
            }

            result.XmlContent = BuildXml(result, documentName);

            return result;
        }

        public static DocxExtractionResult ExtractFromBase64(string base64, string documentName = "Document")
        {
            if (string.IsNullOrWhiteSpace(base64))
                throw new ArgumentException("base64 must not be null or empty.", nameof(base64));
            return ExtractFromBytes(Convert.FromBase64String(base64), documentName);
        }

        public static DocxExtractionResult ExtractFromBytes(byte[] docxBytes, string documentName = "Document")
        {
            if (docxBytes == null || docxBytes.Length == 0)
                throw new ArgumentException("docxBytes must not be null or empty.", nameof(docxBytes));

            using (var ms = new MemoryStream(docxBytes, writable: false))
                return Extract(ms, documentName);
        }

        public static string BuildXml(DocxExtractionResult result, string documentName = "Document")
        {
            XElement xmlRoot = new XElement("Document",
                new XAttribute("FileName", SanitizeForXml(documentName)));

            XElement xmlParas = new XElement("Paragraphs",
                new XAttribute("Count", result.Paragraphs.Count));
            for (int i = 0; i < result.Paragraphs.Count; i++)
            {
                xmlParas.Add(new XElement("Paragraph",
                    new XAttribute("Index", i + 1),
                    SanitizeForXml(result.Paragraphs[i])));
            }
            xmlRoot.Add(xmlParas);

            XElement xmlTables = new XElement("Tables",
                new XAttribute("Count", result.Tables.Count));
            for (int t = 0; t < result.Tables.Count; t++)
            {
                TableData table = result.Tables[t];
                XElement xmlTable = new XElement("Table",
                    new XAttribute("Index", t + 1),
                    new XAttribute("Rows",  table.RowCount));

                for (int r = 0; r < table.Rows.Count; r++)
                {
                    XElement xmlRow = new XElement("Row",
                        new XAttribute("Index", r + 1));

                    for (int c = 0; c < table.Rows[r].Count; c++)
                    {
                        xmlRow.Add(new XElement("Cell",
                            new XAttribute("Index", c + 1),
                            SanitizeForXml(table.Rows[r][c])));
                    }
                    xmlTable.Add(xmlRow);
                }
                xmlTables.Add(xmlTable);
            }
            xmlRoot.Add(xmlTables);

            XElement xmlShapes = new XElement("Shapes",
                new XAttribute("Count", result.Shapes.Count));
            foreach (ShapeData shape in result.Shapes)
            {
                XElement xmlShape = new XElement("Shape",
                    new XAttribute("Id",   SanitizeForXml(shape.Id)),
                    new XAttribute("Name", SanitizeForXml(shape.Name)),
                    SanitizeForXml(shape.TextInside));
                xmlShapes.Add(xmlShape);
            }
            xmlRoot.Add(xmlShapes);

            XElement xmlImages = new XElement("Images",
                new XAttribute("Count", result.Images.Count));
            for (int i = 0; i < result.Images.Count; i++)
            {
                ImageData img = result.Images[i];
                xmlImages.Add(new XElement("Image",
                    new XAttribute("Index",       i + 1),
                    new XAttribute("FileName",    SanitizeForXml(img.FileName)),
                    new XAttribute("ContentType", SanitizeForXml(img.ContentType)),
                    new XAttribute("SizeBytes",   img.Bytes.Length)));
            }
            xmlRoot.Add(xmlImages);

            XDocument xDoc = new XDocument(
                new XDeclaration("1.0", "utf-8", "yes"),
                xmlRoot);

            using (var ms = new MemoryStream())
            using (var xw = System.Xml.XmlWriter.Create(ms, new System.Xml.XmlWriterSettings
            {
                Encoding           = System.Text.Encoding.UTF8,
                Indent             = true,
                IndentChars        = "  ",
                OmitXmlDeclaration = false
            }))
            {
                xDoc.Save(xw);
                xw.Flush();
                return System.Text.Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        private static string SanitizeForXml(string? text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return Regex.Replace(text, @"[\x00-\x08\x0B\x0C\x0E-\x1F\x7F\uFFFE\uFFFF]", " ");
        }

        private static void PrintUsage()
        {
            Console.WriteLine("================================================================");
            Console.WriteLine("  GTA.DocxLib v2 -- Usage");
            Console.WriteLine("================================================================");
            Console.WriteLine("  Extracts .docx content to a folder:");
            Console.WriteLine("    {filename}_LLM.xml   <- all text, tables, shapes");
            Console.WriteLine("    image1.png, ...      <- embedded images");
            Console.WriteLine("  Extracts: paragraphs, tables, shapes (VML+DML), images.");
            Console.WriteLine("");
            Console.WriteLine("  args[0] = full path to the .docx file");
            Console.WriteLine("================================================================");
        }
    }
}
