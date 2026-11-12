using System.Globalization;
using System.Text.RegularExpressions;

namespace GTA.Tools
{
    // Base of the outlook_* tools: shared formatting and date parsing. The real work is in GTA.Tools.Impl.Outlook.OutlookApi.
    internal static partial class ToolsOutlookApi
    {
        private static readonly Regex DateOnlyPattern = new(@"^\d{4}-\d{1,2}-\d{1,2}$", RegexOptions.Compiled);

        internal static string FormatError(string label, Exception ex)
            => $"❌ {label}\nError: {ex.Message}";

        /// <summary>
        /// Parses "yyyy-MM-dd" or "yyyy-MM-dd HH:mm[:ss]" (this PC's time zone) or a full ISO 8601 time with offset.
        /// A date without a time means the START of that day for 'date_from' and the END of that day for 'date_to'.
        /// Empty text is not an error: it gives null (open end).
        /// </summary>
        internal static bool TryParseBoundary(string? text, bool isEnd, out DateTimeOffset? utc, out string? error)
        {
            utc = null;
            error = null;
            if (string.IsNullOrWhiteSpace(text)) return true;

            string trimmed = text.Trim();
            if (!DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTimeOffset parsed))
            {
                error = $"Cannot read the date '{trimmed}'. Use 'yyyy-MM-dd' or 'yyyy-MM-dd HH:mm' (example: 2026-11-01).";
                return false;
            }

            if (isEnd && DateOnlyPattern.IsMatch(trimmed))
                parsed = parsed.AddDays(1).AddTicks(-1);

            utc = parsed.ToUniversalTime();
            return true;
        }

        /// <summary>Time in this PC's time zone, as text.</summary>
        internal static string LocalTime(DateTimeOffset utc)
            => TimeZoneInfo.ConvertTime(utc, TimeZoneInfo.Local).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        internal static string LocalZoneLabel()
        {
            TimeSpan offset = TimeZoneInfo.Local.GetUtcOffset(DateTime.Now);
            return $"UTC{(offset < TimeSpan.Zero ? "-" : "+")}{offset:hh\\:mm}";
        }

        internal static string FormatSize(long bytes)
            => bytes >= 1_048_576 ? $"{bytes / 1_048_576.0:F1} MB"
             : bytes >= 1_024     ? $"{bytes / 1_024.0:F1} KB"
             :                      $"{bytes} B";
    }
}
