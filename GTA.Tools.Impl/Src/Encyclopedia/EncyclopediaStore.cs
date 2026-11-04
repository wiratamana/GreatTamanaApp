using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace GTA.Tools.Impl.Encyclopedia
{
    public static class EncyclopediaStore
    {
        /// <summary>Root folder where every encyclopedia entry file is stored.</summary>
        public static string RootDir => Path.Combine(AppContext.BaseDirectory, "encyclopedia_kb");

        private static readonly Regex InvalidCharsRegex =
            new(@"[^a-zA-Z0-9_\-]+", RegexOptions.Compiled);

        public static string SanitizeTitle(string title)
        {
            string trimmed = title.Trim().Replace(' ', '_');
            string cleaned = InvalidCharsRegex.Replace(trimmed, "");
            if (cleaned.Length == 0) cleaned = "untitled";
            if (cleaned.Length > 80) cleaned = cleaned[..80];
            return cleaned;
        }

        public static string BuildEntryPath(string title, DateTime dateWrite)
        {
            Directory.CreateDirectory(RootDir);

            string datePart  = dateWrite.ToString("yyyy-MM-dd");
            string safeTitle = SanitizeTitle(title);
            string baseName  = $"{datePart}_{safeTitle}";
            string path      = Path.Combine(RootDir, baseName + ".md");

            int suffix = 2;
            while (File.Exists(path))
            {
                path = Path.Combine(RootDir, $"{baseName}_{suffix}.md");
                suffix++;
            }
            return path;
        }

        /// <summary>All entry file paths currently on disk, sorted by filename (i.e. chronologically, oldest first).</summary>
        public static List<string> ListEntryFiles()
        {
            if (!Directory.Exists(RootDir)) return new List<string>();
            return Directory.GetFiles(RootDir, "*.md")
                .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static string BuildFullCorpus(out int entryCount)
        {
            List<string> files = ListEntryFiles();
            entryCount = files.Count;
            if (files.Count == 0) return "";

            var sb = new StringBuilder();
            foreach (string file in files)
            {
                string content;
                try { content = File.ReadAllText(file, Encoding.UTF8); }
                catch (Exception ex) { content = $"[Error reading file: {ex.Message}]"; }

                sb.AppendLine("=== ENTRY: " + Path.GetFileName(file) + " ===");
                sb.AppendLine(content.TrimEnd());
                sb.AppendLine("=== END ENTRY ===");
                sb.AppendLine();
            }
            return sb.ToString();
        }
    }
}
