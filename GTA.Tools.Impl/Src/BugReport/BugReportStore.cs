using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace GTA.Tools.Impl.BugReport
{
    public static class BugReportStore
    {
        /// <summary>Root folder where every bug report file is stored (under the running exe).</summary>
        public static string RootDir => Path.Combine(AppContext.BaseDirectory, "BugReports");

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

        public static string BuildEntryPath(string title, DateTime dateNow)
        {
            Directory.CreateDirectory(RootDir);

            string datePart  = dateNow.ToString("yyyy-MM-dd");
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

        /// <summary>All bug report entry file paths currently on disk, sorted by filename (chronological, oldest first).</summary>
        public static List<string> ListEntryFiles()
        {
            if (!Directory.Exists(RootDir)) return new List<string>();
            return Directory.GetFiles(RootDir, "*.md")
                .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
