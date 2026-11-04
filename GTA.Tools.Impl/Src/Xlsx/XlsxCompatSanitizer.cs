using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace GTA.XlsxLib
{
    internal static class XlsxCompatSanitizer
    {
        /// <summary>Returns xlsxBytes unchanged if ClosedXML can already open it as-is.</summary>
        internal static byte[] EnsureLoadable(byte[]? xlsxBytes)
        {
            if (xlsxBytes == null || xlsxBytes.Length == 0)
                return xlsxBytes ?? Array.Empty<byte>();

            try
            {
                using var probeStream = new MemoryStream(xlsxBytes, writable: false);
                using var probe = new ClosedXML.Excel.XLWorkbook(probeStream);
                return xlsxBytes; // opens fine as-is — nothing to repair
            }
            catch (Exception ex) when (LooksLikePhoneticRunIssue(ex))
            {
                try
                {
                    return StripPhoneticRuns(xlsxBytes);
                }
                catch
                {
                    return xlsxBytes;
                }
            }
            catch
            {
                // Some other problem entirely (genuine corruption, wrong file type,
                // password protection, ...) — not ours to fix. Leave bytes untouched.
                return xlsxBytes;
            }
        }

        private static bool LooksLikePhoneticRunIssue(Exception ex)
            => ex.Message != null && ex.Message.IndexOf("phonetic", StringComparison.OrdinalIgnoreCase) >= 0;

        private static byte[] StripPhoneticRuns(byte[] xlsxBytes)
        {
            using var input  = new MemoryStream(xlsxBytes, writable: false);
            using var output = new MemoryStream();

            using (var zipIn  = new ZipArchive(input, ZipArchiveMode.Read))
            using (var zipOut = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (ZipArchiveEntry entry in zipIn.Entries)
                {
                    ZipArchiveEntry newEntry = zipOut.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                    try { newEntry.LastWriteTime = entry.LastWriteTime; } catch { /* best-effort, cosmetic only */ }

                    bool isSharedStrings = string.Equals(
                        entry.FullName, "xl/sharedStrings.xml", StringComparison.OrdinalIgnoreCase);

                    using Stream entryIn  = entry.Open();
                    using Stream entryOut = newEntry.Open();

                    if (!isSharedStrings)
                    {
                        entryIn.CopyTo(entryOut);
                        continue;
                    }

                    XDocument doc = XDocument.Load(entryIn, LoadOptions.PreserveWhitespace);
                    doc.Descendants()
                       .Where(e => e.Name.LocalName == "rPh" || e.Name.LocalName == "phoneticPr")
                       .ToList()
                       .ForEach(e => e.Remove());

                    // Keep sharedStrings.xml as UTF-8 with no BOM, matching what
                    // ClosedXML/Excel themselves write.
                    using var writer = new StreamWriter(entryOut, new UTF8Encoding(false));
                    doc.Save(writer, SaveOptions.DisableFormatting);
                }
            }

            return output.ToArray();
        }
    }
}
