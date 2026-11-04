using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;

namespace GTA.Tools
{
    internal static class ToolsArchive
    {
        private const long MAX_EXTRACT_ENTRY_BYTES = 512L * 1024 * 1024;
        private const long MAX_EXTRACT_TOTAL_BYTES = 2L * 1024 * 1024 * 1024;

        // ── zip_files ─────────────────────────────────────────────────────────────

        [ToolTag("archive")]
        [FileMutating]
        [ToolInfo(
            name: "zip_files",
            description: "Compress a file or directory into a .zip archive. " +
                         "If source_path is a directory, its contents are included (optionally recursively). " +
                         "Creates any missing parent directories for the output archive. " +
                         "Uses optimal compression. No external tools or packages required.")]
        [Parameter(index: 0, name: "source_path",
            type: "string",
            description: "Full absolute Windows path of the file or directory to compress.",
            required: true)]
        [Parameter(index: 1, name: "dest_path",
            type: "string",
            description: "Full absolute Windows path of the output .zip file to create.",
            required: true)]
        [Parameter(index: 2, name: "overwrite",
            type: "boolean",
            description: "If true (default), overwrite the destination .zip file if it already exists.",
            required: false)]
        [Parameter(index: 3, name: "recursive",
            type: "boolean",
            description: "When source_path is a directory: if false, only top-level files are included. " +
                         "Defaults to true (include all subdirectories).",
            required: false)]
        public static object ZipFiles(JsonObject input)
        {
            string? rawSrc = input["source_path"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(rawSrc))
                return "Error: 'source_path' parameter is missing or empty.";
            string src = ToolsIOHelpers.CleanPath(rawSrc);

            bool srcIsFile = File.Exists(src);
            bool srcIsDir  = Directory.Exists(src);

            if (!srcIsFile && !srcIsDir)
                return $"Error: Source not found at '{src}'.";

            string? rawDest = input["dest_path"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(rawDest))
                return "Error: 'dest_path' parameter is missing or empty.";
            string dest = ToolsIOHelpers.CleanPath(rawDest);

            if (Directory.Exists(dest))
                return $"Error: Destination '{dest}' is an existing directory. Provide a full .zip file path.";

            bool overwrite = ToolsIOHelpers.GetOptionalBool(input, "overwrite", true);
            bool recursive = ToolsIOHelpers.GetOptionalBool(input, "recursive", true);

            if (!overwrite && File.Exists(dest))
                return $"Error: Destination '{dest}' already exists and overwrite=false.";

            try
            {
                string? destDir = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(destDir))
                    Directory.CreateDirectory(destDir);

                string tempDest = dest + ".tmp_" + Guid.NewGuid().ToString("N") + ".zip";
                int fileCount = 0;

                try
                {
                    if (srcIsDir)
                    {
                        SearchOption searchOpt = recursive
                            ? SearchOption.AllDirectories
                            : SearchOption.TopDirectoryOnly;

                        string destFullPath = Path.GetFullPath(dest);

                        const long MAX_ZIP_SOURCE_BYTES = 2L * 1024 * 1024 * 1024;
                        var  filesToZip = new System.Collections.Generic.List<string>();
                        long srcSize    = 0;
                        foreach (string f in Directory.EnumerateFiles(src, "*", searchOpt))
                        {
                            if (string.Equals(Path.GetFullPath(f), destFullPath,
                                              StringComparison.OrdinalIgnoreCase))
                                continue;
                            filesToZip.Add(f);
                            try { srcSize += new FileInfo(f).Length; } catch { }
                        }

                        if (srcSize > MAX_ZIP_SOURCE_BYTES)
                            return $"Error: Source directory is too large to zip ({ToolsIOHelpers.FormatBytes(srcSize)}). " +
                                   $"Maximum allowed source size is {ToolsIOHelpers.FormatBytes(MAX_ZIP_SOURCE_BYTES)}.";

                        using var archive = ZipFile.Open(tempDest, ZipArchiveMode.Create);

                        foreach (string file in filesToZip)
                        {
                            string entryName = Path.GetRelativePath(src, file).Replace('\\', '/');
                            archive.CreateEntryFromFile(file, entryName, CompressionLevel.Optimal);
                            fileCount++;
                        }
                    }
                    else
                    {
                        using var archive = ZipFile.Open(tempDest, ZipArchiveMode.Create);
                        archive.CreateEntryFromFile(src, Path.GetFileName(src), CompressionLevel.Optimal);
                        fileCount = 1;
                    }

                    File.Move(tempDest, dest, overwrite: true);
                }
                catch
                {
                    try { File.Delete(tempDest); } catch { }
                    throw;
                }

                long zipSize = new FileInfo(dest).Length;
                return $"Created archive '{dest}' ({ToolsIOHelpers.FormatBytes(zipSize)}) " +
                       $"containing {fileCount} file(s) from '{src}'.";
            }
            catch (UnauthorizedAccessException) { return $"Error: Access denied while creating archive '{dest}'."; }
            catch (InvalidDataException ex)     { return $"Error: Archive format error: {ex.Message}"; }
            catch (IOException ex)              { return $"Error: IO error while creating archive: {ex.Message}"; }
            catch (Exception ex)                { return $"Error: Unexpected error while zipping: {ex.Message}"; }
        }

        // ── extract_zip ───────────────────────────────────────────────────────────

        [ToolTag("archive")]
        [FileMutating]
        [ToolInfo(
            name: "extract_zip",
            description: "Extract the contents of a .zip archive to a target directory. " +
                         "Creates the target directory if it does not exist. " +
                         "Includes zip-slip protection to prevent path-traversal attacks. " +
                         "By default does NOT overwrite existing files at the destination.")]
        [Parameter(index: 0, name: "source_path",
            type: "string",
            description: "Full absolute Windows path of the .zip file to extract.",
            required: true)]
        [Parameter(index: 1, name: "dest_path",
            type: "string",
            description: "Full absolute Windows path of the directory to extract into.",
            required: true)]
        [Parameter(index: 2, name: "overwrite",
            type: "boolean",
            description: "If true, overwrite existing files at the destination. " +
                         "Defaults to false (skip files that already exist).",
            required: false)]
        public static object ExtractZip(JsonObject input)
        {
            string? rawSrc = input["source_path"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(rawSrc))
                return "Error: 'source_path' parameter is missing or empty.";
            string src = ToolsIOHelpers.CleanPath(rawSrc);

            if (Directory.Exists(src))
                return $"Error: '{src}' is a directory, not a .zip file.";
            if (!File.Exists(src))
                return $"Error: File not found at '{src}'.";

            string? rawDest = input["dest_path"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(rawDest))
                return "Error: 'dest_path' parameter is missing or empty.";
            string dest = ToolsIOHelpers.CleanPath(rawDest);

            if (File.Exists(dest))
                return $"Error: Destination '{dest}' is an existing file. Provide a directory path.";

            bool overwrite = ToolsIOHelpers.GetOptionalBool(input, "overwrite", false);

            try
            {
                bool destPreExisted = Directory.Exists(dest);

                void CleanupOnAbort()
                {
                    if (!destPreExisted)
                        try { Directory.Delete(dest, recursive: true); } catch { }
                }

                Directory.CreateDirectory(dest);

                string destCanonical = Path.GetFullPath(dest)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;

                int  extracted         = 0;
                int  skipped           = 0;
                long totalUncompressed = 0;

                using var archive = ZipFile.OpenRead(src);

                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;

                    string relativeName = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                    string fullDest     = Path.GetFullPath(Path.Combine(dest, relativeName));

                    if (!fullDest.StartsWith(destCanonical, StringComparison.OrdinalIgnoreCase))
                    {
                        CleanupOnAbort();
                        return $"Error: Archive entry '{entry.FullName}' would extract outside the " +
                               $"destination directory (zip-slip attack detected). Extraction aborted.";
                    }

                    if (entry.Length > MAX_EXTRACT_ENTRY_BYTES)
                    {
                        CleanupOnAbort();
                        return $"Error: Entry '{entry.FullName}' declares an uncompressed size of " +
                               $"{ToolsIOHelpers.FormatBytes(entry.Length)}, which exceeds the per-entry limit " +
                               $"({ToolsIOHelpers.FormatBytes(MAX_EXTRACT_ENTRY_BYTES)}). Extraction aborted.";
                    }

                    string? parentDir = Path.GetDirectoryName(fullDest);
                    if (!string.IsNullOrEmpty(parentDir))
                        Directory.CreateDirectory(parentDir);

                    FileStream? destFs = null;
                    try
                    {
                        FileMode fileMode = overwrite ? FileMode.Create : FileMode.CreateNew;
                        try
                        {
                            destFs = new FileStream(fullDest, fileMode, FileAccess.Write, FileShare.None);
                        }
                        catch (IOException)
                        {
                            if (!overwrite) { skipped++; continue; }
                            throw;
                        }

                        using var entryStream = entry.Open();
                        byte[] buf = new byte[81920];
                        int    read;
                        long   entryWritten = 0;

                        while ((read = entryStream.Read(buf, 0, buf.Length)) > 0)
                        {
                            entryWritten      += read;
                            totalUncompressed += read;

                            if (entryWritten > MAX_EXTRACT_ENTRY_BYTES)
                            {
                                destFs.Dispose(); destFs = null;
                                try { File.Delete(fullDest); } catch { }
                                CleanupOnAbort();
                                return $"Error: Entry '{entry.FullName}' exceeds the per-entry size limit during extraction. Aborted.";
                            }
                            if (totalUncompressed > MAX_EXTRACT_TOTAL_BYTES)
                            {
                                destFs.Dispose(); destFs = null;
                                try { File.Delete(fullDest); } catch { }
                                CleanupOnAbort();
                                return $"Error: Total extracted size exceeds the limit " +
                                       $"({ToolsIOHelpers.FormatBytes(MAX_EXTRACT_TOTAL_BYTES)}). Aborted.";
                            }

                            destFs.Write(buf, 0, read);
                        }
                        extracted++;
                    }
                    finally
                    {
                        destFs?.Dispose();
                    }
                }

                string msg = $"Extracted {extracted} file(s) from '{src}' to '{dest}'.";
                if (skipped > 0)
                    msg += $" Skipped {skipped} existing file(s) (overwrite=false).";

                return msg;
            }
            catch (UnauthorizedAccessException) { return $"Error: Access denied while extracting to '{dest}'."; }
            catch (InvalidDataException ex)     { return $"Error: Invalid or corrupt zip archive: {ex.Message}"; }
            catch (IOException ex)              { return $"Error: IO error while extracting: {ex.Message}"; }
            catch (Exception ex)                { return $"Error: Unexpected error while extracting: {ex.Message}"; }
        }
    }
}
