using System;
using System.Collections.Generic;
using System.IO;
using ClosedXML.Excel;
using ClosedXML.Excel.Drawings;

namespace GTA.XlsxLib
{
    public partial class XlsxExtractorTool
    {
        private static int ExtractImagesWithClosedXml(string xlsxPath, string outputFolder)
        {
            int count = 0;
            try
            {
                using var ms       = new MemoryStream(XlsxCompatSanitizer.EnsureLoadable(File.ReadAllBytes(xlsxPath)), writable: false);
                using var workbook = new XLWorkbook(ms);
                foreach (IXLWorksheet worksheet in workbook.Worksheets)
                {
                    foreach (IXLPicture picture in worksheet.Pictures)
                    {
                        try
                        {
                            string cellAddress = picture.TopLeftCell.Address.ToString();
                            string extension   = GetImageExtension(picture.Format.ToString());
                            string rawName     = $"{worksheet.Name}_{cellAddress}_{picture.Name}";
                            string safeName    = MakeSafeFileName(rawName) + extension;
                            string savePath    = Path.Combine(outputFolder, safeName);

                            using var fs = new FileStream(savePath, FileMode.Create, FileAccess.Write);
                            picture.ImageStream.CopyTo(fs);

                            Console.WriteLine($"  [Image] Saved: {safeName}  (Sheet: {worksheet.Name}, Cell: {cellAddress})");
                            count++;
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"  [Image] WARNING: Could not save '{picture.Name}': {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ERROR (ClosedXML): {ex.Message}");
            }
            return count;
        }

        private static List<XlsxImageResult> ExtractImagesFromStream(Stream stream)
        {
            var images = new List<XlsxImageResult>();
            try
            {
                using var bufferedMs = new MemoryStream();
                stream.CopyTo(bufferedMs);
                using var ms = new MemoryStream(XlsxCompatSanitizer.EnsureLoadable(bufferedMs.ToArray()), writable: false);
                using var workbook = new XLWorkbook(ms);
                foreach (IXLWorksheet worksheet in workbook.Worksheets)
                {
                    foreach (IXLPicture picture in worksheet.Pictures)
                    {
                        try
                        {
                            string cellAddress = picture.TopLeftCell.Address.ToString();
                            string ext         = GetImageExtension(picture.Format.ToString());
                            string rawName     = $"{worksheet.Name}_{cellAddress}_{picture.Name}";
                            string safeName    = MakeSafeFileName(rawName) + ext;

                            var imgBytes = new MemoryStream();
                            picture.ImageStream.CopyTo(imgBytes);

                            images.Add(new XlsxImageResult
                            {
                                SheetName   = worksheet.Name,
                                CellAddress = cellAddress,
                                Name        = safeName,
                                MediaType   = ImageExtToMediaType(ext),
                                Bytes       = imgBytes.ToArray()
                            });
                        }
                        catch { /* skip individual picture errors */ }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[XlsxExtractor] Image extraction warning: {ex.Message}");
            }
            return images;
        }
    }
}
