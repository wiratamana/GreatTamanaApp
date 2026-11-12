using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using GTA.Tools.Impl.Teams;

namespace GTA.Tools
{
    internal static partial class ToolsTeamsApi
    {
        private const int DefaultFreeTimeDays = 14;
        private const int MaxFreeSlotsShown = 150;

        [ToolTag("teams_api")]
        [ToolInfo(
            name: "teams_find_free_time",
            description:
                "Find the best time to hold a MEETING with several people: it reads everybody's Microsoft 365 / Outlook calendar and returns " +
                "the time windows where ALL attendees are free (or all but 'max_unavailable' of them). Read only: it does not create a meeting. " +
                "Rules: only Monday-Friday inside the work hours (default 09:00-18:00, this PC's time zone); Japanese public holidays are skipped " +
                "(calculated, not looked up; company holidays are NOT known: pass them in 'skip_dates'); " +
                "the past is never offered; cancelled events and events the person declined do not count; " +
                "events shown as free or workingElsewhere do not block; tentative events block unless tentative_is_busy=false; " +
                "an all-day busy / out-of-office event blocks the whole day. " +
                "'users' = e-mail addresses separated by ',' (at most 20; add 'me' for the signed-in user). " +
                "If you only know names, look up the e-mails with teams_fetch_user_name first. " +
                "SAFETY: if a calendar cannot be read (no permission, wrong address) or is too big, the tool REFUSES to answer and says who, " +
                "because a missing calendar would look like free time. Use ignore_unreadable=true only when the user accepts that. " +
                "If no window is found, try a shorter duration_minutes, max_unavailable=1 (then each window lists who is busy), " +
                "tentative_is_busy=false, or a longer period. " +
                "Typical use: users='a@x.com,b@x.com,me', duration_minutes=60, date_from/date_to = the period the user wants, " +
                "daily_blocked='12:00-13:00' to keep lunch free. " +
                "Login is automatic and reuses a saved token. PRIVACY: it reveals only free windows, no event subjects.")]
        [Parameter(index: 0, name: "users",
            type: "string",
            description: "E-mail addresses / user principal names of all attendees, separated by ','. 'me' = the signed-in user. " +
                         "Example: 'tito@hhq.suzuki.co.jp,hinog@hhq.suzuki.co.jp,me'.",
            required: true)]
        [Parameter(index: 1, name: "date_from",
            type: "string",
            description: "Optional. First day to search, 'yyyy-MM-dd'. Default: today.",
            required: false)]
        [Parameter(index: 2, name: "date_to",
            type: "string",
            description: "Optional. Last day to search (included), 'yyyy-MM-dd'. Default: 13 days after date_from (two weeks). Longest range: 62 days.",
            required: false)]
        [Parameter(index: 3, name: "duration_minutes",
            type: "integer",
            description: "Optional. Length of the meeting in minutes. Only windows at least this long are returned. Default: 30. Minimum: 5.",
            required: false)]
        [Parameter(index: 4, name: "work_start",
            type: "string",
            description: "Optional. Earliest meeting start every day, 'HH:mm'. Default: 09:00.",
            required: false)]
        [Parameter(index: 5, name: "work_end",
            type: "string",
            description: "Optional. Latest meeting end every day, 'HH:mm'. Default: 18:00.",
            required: false)]
        [Parameter(index: 6, name: "max_unavailable",
            type: "integer",
            description: "Optional. How many attendees may be busy in a returned window. Default: 0 (everybody must be free). " +
                         "With 1 or more, each window shows who is busy.",
            required: false)]
        [Parameter(index: 7, name: "tentative_is_busy",
            type: "boolean",
            description: "Optional. true (default) = a tentative event blocks the time (safe). false = tentative events count as free.",
            required: false)]
        [Parameter(index: 8, name: "skip_japanese_holidays",
            type: "boolean",
            description: "Optional. true (default) = never offer Japanese public holidays. Works for the years 2022-2099.",
            required: false)]
        [Parameter(index: 9, name: "skip_dates",
            type: "string",
            description: "Optional. Extra days that must never be offered (company holidays, and so on), 'yyyy-MM-dd', separated by ','. " +
                         "Example: '2026-12-29,2026-12-30'.",
            required: false)]
        [Parameter(index: 10, name: "daily_blocked",
            type: "string",
            description: "Optional. Time ranges that are blocked EVERY day for everybody, 'HH:mm-HH:mm', separated by ','. " +
                         "Example: lunch '12:00-13:00'.",
            required: false)]
        [Parameter(index: 11, name: "ignore_unreadable",
            type: "boolean",
            description: "Optional. true = attendees whose calendar cannot be read are left out and listed as NOT checked. " +
                         "Default: false (the tool refuses to answer instead).",
            required: false)]
        public static object TeamsFindFreeTime(JsonObject input)
        {
            if (!TrySplitUsers(input["users"]?.GetValue<string>(), out List<string> users, out string? usersError))
                return $"Error: 'users': {usersError}";

            DateTime fromDate = DateTime.Today;
            string? fromText = input["date_from"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(fromText) && !TryParseDay(fromText, out fromDate))
                return $"Error: 'date_from': cannot read '{fromText}'. Use 'yyyy-MM-dd' (example: 2026-11-24).";

            DateTime toDate = fromDate.AddDays(DefaultFreeTimeDays - 1);
            string? toText = input["date_to"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(toText) && !TryParseDay(toText, out toDate))
                return $"Error: 'date_to': cannot read '{toText}'. Use 'yyyy-MM-dd' (example: 2026-11-30).";
            if (toDate < fromDate) return "Error: 'date_to' is before 'date_from'.";
            if ((toDate - fromDate).TotalDays + 1 > TeamsApi.HardMaxCalendarDays)
                return $"Error: the date range is too long (limit {TeamsApi.HardMaxCalendarDays} days).";

            TimeSpan workStart = TimeSpan.FromHours(9), workEnd = TimeSpan.FromHours(18);
            string? wsText = input["work_start"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(wsText) && !TryParseClock(wsText, out workStart))
                return $"Error: 'work_start': cannot read '{wsText}'. Use 'HH:mm' (example: 09:30).";
            string? weText = input["work_end"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(weText) && !TryParseClock(weText, out workEnd))
                return $"Error: 'work_end': cannot read '{weText}'. Use 'HH:mm' (example: 17:30).";
            if (workEnd <= workStart) return "Error: 'work_end' must be later than 'work_start'.";

            int duration = ReadInt(input, "duration_minutes", 30);
            if (duration < 5) return "Error: 'duration_minutes' must be at least 5.";
            if (duration > (workEnd - workStart).TotalMinutes)
                return $"Error: 'duration_minutes' ({duration}) is longer than the work hours ({workStart:hh\\:mm}-{workEnd:hh\\:mm}).";

            int maxUnavailable = ReadInt(input, "max_unavailable", 0);
            if (maxUnavailable < 0) return "Error: 'max_unavailable' cannot be negative.";

            var skipDates = new List<DateTime>();
            string? skipText = input["skip_dates"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(skipText))
            {
                foreach (string part in skipText.Split(new[] { ',', ';', '|', ' ', '\r', '\n' },
                             StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!TryParseDay(part, out DateTime d))
                        return $"Error: 'skip_dates': cannot read '{part}'. Use 'yyyy-MM-dd', separated by ','.";
                    skipDates.Add(d);
                }
            }

            var blocked = new List<(TimeSpan Start, TimeSpan End)>();
            string? blockedText = input["daily_blocked"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(blockedText))
            {
                foreach (string part in blockedText.Split(new[] { ',', ';', '|' },
                             StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    string[] ends = part.Split('-', StringSplitOptions.TrimEntries);
                    if (ends.Length != 2 || !TryParseClock(ends[0], out TimeSpan bs) || !TryParseClock(ends[1], out TimeSpan be) || be <= bs)
                        return $"Error: 'daily_blocked': cannot read '{part}'. Use 'HH:mm-HH:mm' (example: 12:00-13:00), separated by ','.";
                    blocked.Add((bs, be));
                }
            }

            var req = new TeamsFreeTimeRequest
            {
                Users = users,
                FromDate = fromDate,
                ToDate = toDate,
                WorkStart = workStart,
                WorkEnd = workEnd,
                DurationMinutes = duration,
                MaxUnavailable = maxUnavailable,
                TentativeIsBusy = ReadBool(input, "tentative_is_busy", true),
                SkipJapaneseHolidays = ReadBool(input, "skip_japanese_holidays", true),
                SkipDates = skipDates,
                DailyBlocked = blocked,
                IgnoreUnreadable = ReadBool(input, "ignore_unreadable", false),
            };

            TeamsFreeTimeResult result;
            try
            {
                result = TeamsApi.FindFreeTime(req);
            }
            catch (Exception ex)
            {
                return FormatError("teams_find_free_time", ex);
            }

            return FormatFreeTime(req, result);
        }

        private static string FormatFreeTime(TeamsFreeTimeRequest req, TeamsFreeTimeResult result)
        {
            var sb = new StringBuilder();
            bool partial = result.Unreadable.Count > 0;
            int days = result.Slots.Select(s => s.Start.Date).Distinct().Count();

            sb.AppendLine(result.Slots.Count == 0
                ? $"❌ No free window found for {result.CheckedUsers.Count} attendee(s)."
                : $"{(partial ? "⚠" : "✅")} {result.Slots.Count} free window(s) on {days} day(s) for {result.CheckedUsers.Count} attendee(s): " +
                  string.Join(", ", result.CheckedUsers));
            sb.AppendLine($"Period {req.FromDate:yyyy-MM-dd} .. {req.ToDate:yyyy-MM-dd} (time zone {LocalZoneLabel()}) | " +
                          $"work hours {req.WorkStart:hh\\:mm}-{req.WorkEnd:hh\\:mm} | meeting length {req.DurationMinutes} min | " +
                          (req.MaxUnavailable == 0 ? "everybody must be free" : $"up to {req.MaxUnavailable} attendee(s) may be busy") +
                          $" | tentative events count as {(req.TentativeIsBusy ? "busy" : "free")}");
            if (req.DailyBlocked.Count > 0)
                sb.AppendLine("Blocked every day: " + string.Join(", ", req.DailyBlocked.Select(b => $"{b.Start:hh\\:mm}-{b.End:hh\\:mm}")));

            foreach (TeamsCalendarResult u in result.Unreadable)
            {
                sb.AppendLine($"⚠ NOT CHECKED: {u.User} — {(u.Error?.TrimEnd('.', ' ') ?? "calendar too big, cut short")}. " +
                              "This person is NOT in the result below. Their time may be busy.");
            }

            if (result.SkippedDays.Count > 0)
                sb.AppendLine("Skipped working days: " + string.Join(", ", result.SkippedDays.Select(kv => $"{kv.Key:yyyy-MM-dd} ({kv.Value})")));
            sb.AppendLine("Events that block time, per attendee: " +
                          string.Join(", ", result.BlockingEventCounts.Select(kv => $"{kv.Key} {kv.Value}")));
            sb.AppendLine();

            int shown = 0;
            DateTime currentDay = DateTime.MinValue;
            foreach (TeamsFreeSlot slot in result.Slots)
            {
                if (shown >= MaxFreeSlotsShown)
                {
                    sb.AppendLine($"… {result.Slots.Count - shown} more window(s) not shown. Use a shorter period or a longer duration_minutes.");
                    break;
                }
                if (slot.Start.Date != currentDay)
                {
                    currentDay = slot.Start.Date;
                    sb.AppendLine($"{currentDay:yyyy-MM-dd} ({currentDay.ToString("ddd", CultureInfo.InvariantCulture)})");
                }
                sb.Append($"  {slot.Start:HH:mm}-{slot.End:HH:mm} ({slot.Minutes} min)");
                if (req.MaxUnavailable > 0)
                    sb.Append(slot.BusyUsers.Count == 0 ? " — everybody free" : " — busy: " + string.Join(", ", slot.BusyUsers));
                sb.AppendLine();
                shown++;
            }

            if (result.Slots.Count == 0)
            {
                sb.AppendLine("Ideas: shorter duration_minutes, max_unavailable=1 (shows who is busy), tentative_is_busy=false, a longer period, wider work hours.");
            }
            sb.AppendLine();
            sb.Append("Note: a window means the calendars show no blocking event. It does not know about company holidays (use skip_dates), " +
                      "travel time, or things that are not in the calendar. Windows are not booked.");
            return sb.ToString().TrimEnd();
        }

        private static bool TryParseDay(string? text, out DateTime day)
            => DateTime.TryParseExact(text?.Trim(), new[] { "yyyy-MM-dd", "yyyy-M-d", "yyyy/MM/dd", "yyyy/M/d" },
                CultureInfo.InvariantCulture, DateTimeStyles.None, out day);

        /// <summary>'HH:mm' or 'H:mm'. '24:00' means the end of the day.</summary>
        private static bool TryParseClock(string? text, out TimeSpan time)
        {
            time = default;
            string t = text?.Trim() ?? "";
            if (t == "24:00") { time = TimeSpan.FromHours(24); return true; }
            return TimeSpan.TryParseExact(t, new[] { @"h\:mm", @"hh\:mm" }, CultureInfo.InvariantCulture, out time)
                   && time >= TimeSpan.Zero && time < TimeSpan.FromHours(24);
        }
    }
}
