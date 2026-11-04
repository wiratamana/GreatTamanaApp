using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GTA.Tools.Impl.IO
{
    public static class FileIoUtils
    {
        public static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        /// <summary>UTF-8 encoding that DOES emit a leading byte-order-mark (BOM), for callers that explicitly opt into it.</summary>
        public static readonly Encoding Utf8WithBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

        /// <summary>Maximum file size allowed for full in-memory reads (50 MB).</summary>
        public const long MAX_READ_BYTES = 50L * 1024 * 1024;

        public static string GetMediaType(string filePath) =>
            Path.GetExtension(filePath).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif"            => "image/gif",
                ".webp"           => "image/webp",
                _                 => "image/png"
            };

        /// <summary>Strip surrounding double-quotes and whitespace from a raw path string.</summary>
        public static string CleanPath(string raw)
        {
            string p = raw.Trim();
            if (p.Length >= 2 && p[0] == '"' && p[p.Length - 1] == '"')
                p = p[1..^1];
            return p;
        }

        public static string? ReadLines(string path, out List<string> lines, out bool hadTrailingNewline)
        {
            lines = new List<string>();
            hadTrailingNewline = false;
            try
            {
                var fi = new FileInfo(path);
                if (fi.Length > MAX_READ_BYTES)
                    return $"Error: File is too large to read fully ({FormatBytes(fi.Length)}). " +
                           "Use read_line with start/length to read it in sections, " +
                           "or get_file_line_count to determine how many lines it has.";

                if (fi.Length > 0)
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    fs.Seek(-1, SeekOrigin.End);
                    int lastByte = fs.ReadByte();
                    hadTrailingNewline = (lastByte == '\n' || lastByte == '\r');
                    fs.Seek(0, SeekOrigin.Begin);
                    using var reader = new StreamReader(fs, Encoding.UTF8);
                    string? line;
                    while ((line = reader.ReadLine()) != null)
                        lines.Add(line);
                }
                return null;
            }
            catch (UnauthorizedAccessException)  { return $"Error: Access denied — cannot read '{path}'."; }
            catch (IOException ex)               { return $"Error: IO error while reading '{path}': {ex.Message}"; }
            catch (Exception ex)                 { return $"Error: Unexpected error while reading '{path}': {ex.Message}"; }
        }

        public static string? WriteLines(string path, List<string> lines, bool hadTrailingNewline = true, bool addBom = false)
        {
            string content = string.Join(Environment.NewLine, lines);
            if (hadTrailingNewline) content += Environment.NewLine;
            return WriteAllText(path, content, addBom);
        }

        public static string? WriteAllText(
            string path, string content, bool addBom = false, Action<long, long>? onProgress = null)
        {
            string tempPath = path + ".tmp_" + Guid.NewGuid().ToString("N");
            try
            {
                Encoding encoding = addBom ? Utf8WithBom : Utf8NoBom;
                byte[] preamble = encoding.GetPreamble();
                byte[] bytes    = encoding.GetBytes(content);
                long   total    = preamble.Length + bytes.Length;

                onProgress?.Invoke(0, total);

                if (total < ProgressChunkThresholdBytes)
                {
                    File.WriteAllBytes(tempPath, preamble.Length > 0 ? Combine(preamble, bytes) : bytes);
                }
                else
                {
                    using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        if (preamble.Length > 0)
                            fs.Write(preamble, 0, preamble.Length);

                        long written = preamble.Length;
                        int  offset  = 0;
                        while (offset < bytes.Length)
                        {
                            int chunkSize = Math.Min(ProgressChunkSizeBytes, bytes.Length - offset);
                            fs.Write(bytes, offset, chunkSize);
                            offset  += chunkSize;
                            written += chunkSize;
                            onProgress?.Invoke(written, total);
                        }
                    }
                }

                onProgress?.Invoke(total, total);
                File.Move(tempPath, path, overwrite: true);
                return null;
            }
            catch (UnauthorizedAccessException) { try { File.Delete(tempPath); } catch { } return $"Error: Access denied — cannot write to '{path}'."; }
            catch (IOException ex)              { try { File.Delete(tempPath); } catch { } return $"Error: IO error while writing '{path}': {ex.Message}"; }
            catch (Exception ex)                { try { File.Delete(tempPath); } catch { } return $"Error: Unexpected error while writing '{path}': {ex.Message}"; }
        }

        /// <summary>Below this total byte count, WriteAllText writes in one shot (no chunking overhead).</summary>
        private const int ProgressChunkThresholdBytes = 1024 * 1024; // 1 MB

        /// <summary>Chunk size used by WriteAllText's streaming path for content at/above the threshold.</summary>
        private const int ProgressChunkSizeBytes = 256 * 1024; // 256 KB

        /// <summary>Concatenate two byte arrays (small helper — avoids a LINQ/Span dependency for one call site).</summary>
        private static byte[] Combine(byte[] first, byte[] second)
        {
            var result = new byte[first.Length + second.Length];
            Buffer.BlockCopy(first, 0, result, 0, first.Length);
            Buffer.BlockCopy(second, 0, result, first.Length, second.Length);
            return result;
        }

        /// <summary>Convert a byte count to a human-readable string (e.g. "4.2 KB", "1.3 MB").</summary>
        public static string FormatBytes(long bytes)
        {
            if (bytes < 1024)                return $"{bytes} B";
            if (bytes < 1024 * 1024)         return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
        }

        private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            // Executables / libraries / debug symbols
            ".dll", ".exe", ".pdb", ".so", ".dylib", ".lib", ".a", ".o", ".obj",
            ".node", ".wasm", ".msi", ".sys", ".bin",
            // Archives / packages
            ".zip", ".7z", ".rar", ".gz", ".tar", ".tgz", ".bz2", ".xz", ".iso", ".nupkg", ".vsix",
            // Images
            ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp", ".tiff", ".tif",
            // Audio / video
            ".mp3", ".mp4", ".avi", ".mov", ".wav", ".flac", ".mkv", ".wmv", ".ogg",
            // Documents / data blobs
            ".pdf", ".db", ".sqlite", ".sqlite3", ".mdf", ".ldf", ".pack", ".idx",
            // Fonts
            ".ttf", ".otf", ".woff", ".woff2",
            // Misc compiled artifacts
            ".class", ".pyc", ".pyd",
        };

        public static bool IsBinaryExtension(string path)
            => BinaryExtensions.Contains(Path.GetExtension(path));

        public static bool IsBinaryContent(string path, int sampleSize = 8000)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                Span<byte> buffer = stackalloc byte[Math.Min(sampleSize, 8000)];
                int read = fs.Read(buffer);
                for (int i = 0; i < read; i++)
                {
                    if (buffer[i] == 0)
                        return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }
    }
}
