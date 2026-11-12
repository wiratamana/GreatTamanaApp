
namespace GTA.Tools.Impl.Teams;

/// <summary>
/// Finds the windows where (nearly) everybody is free. Pure calculation, no network.
///
/// Rules:
///   * Only working days (Monday to Friday), inside [WorkStart, WorkEnd]. Japanese public holidays and skip_dates are skipped.
///   * Today: the past cannot be booked, so the window starts at "now" rounded up to the next 5 minutes.
///   * An event blocks time when it is not cancelled, the owner did not DECLINE it, and "showAs" is busy / oof / unknown
///     (tentative only when TentativeIsBusy). "free" and "workingElsewhere" never block.
///   * An all-day event that blocks (for example a vacation shown as busy/oof) blocks the whole day.
///   * Daily blocked ranges (lunch) block everybody.
///   * With MaxUnavailable = N, a window may have up to N busy attendees; each window says who they are.
/// </summary>
internal static class TeamsFreeTime
{
    public static TeamsFreeTimeResult Compute(TeamsFreeTimeRequest req, IReadOnlyList<TeamsCalendarResult> readable, DateTime now)
    {
        var result = new TeamsFreeTimeResult();
        var users = readable.Select(r => r.User).ToList();
        result.CheckedUsers.AddRange(users);

        // Blocking intervals per attendee.
        var intervals = new List<List<(DateTime Start, DateTime End)>>();
        foreach (TeamsCalendarResult cal in readable)
        {
            var list = cal.Events
                .Where(e => Blocks(e, req.TentativeIsBusy) && e.End > e.Start)
                .Select(e => (e.Start, e.End))
                .ToList();
            intervals.Add(list);
            result.BlockingEventCounts[cal.User] = list.Count;
        }

        var skipDates = new HashSet<DateTime>(req.SkipDates.Select(d => d.Date));
        DateTime nowRounded = RoundUp(now, 5);

        for (DateTime day = req.FromDate.Date; day <= req.ToDate.Date; day = day.AddDays(1))
        {
            if (day.DayOfWeek == DayOfWeek.Saturday || day.DayOfWeek == DayOfWeek.Sunday) continue;

            if (skipDates.Contains(day)) { result.SkippedDays[day] = "skip_dates"; continue; }
            if (req.SkipJapaneseHolidays && JapanHolidays.TryGetName(day, out string holiday))
            {
                result.SkippedDays[day] = holiday;
                continue;
            }

            DateTime windowStart = day + req.WorkStart;
            DateTime windowEnd = day + req.WorkEnd;
            if (day == now.Date && nowRounded > windowStart) windowStart = nowRounded;
            if (windowStart >= windowEnd) continue;

            // Everybody's busy time of THIS day, clipped to the window.
            var perUser = new List<List<(DateTime Start, DateTime End)>>();
            foreach (var list in intervals)
                perUser.Add(Clip(list, windowStart, windowEnd));
            var hardBlocked = Clip(req.DailyBlocked.Select(b => (day + b.Start, day + b.End)), windowStart, windowEnd);

            // Cut the window at every start / end, then look at each piece.
            var cuts = new SortedSet<DateTime> { windowStart, windowEnd };
            foreach (var list in perUser) foreach (var (s, e) in list) { cuts.Add(s); cuts.Add(e); }
            foreach (var (s, e) in hardBlocked) { cuts.Add(s); cuts.Add(e); }
            DateTime[] points = cuts.ToArray();

            var dayPieces = new List<TeamsFreeSlot>();
            for (int i = 0; i + 1 < points.Length; i++)
            {
                DateTime a = points[i], b = points[i + 1];
                if (Overlaps(hardBlocked, a, b)) continue;

                var busy = new List<string>();
                for (int u = 0; u < users.Count; u++)
                    if (Overlaps(perUser[u], a, b)) busy.Add(users[u]);
                if (busy.Count > req.MaxUnavailable) continue;

                busy.Sort(StringComparer.OrdinalIgnoreCase);
                TeamsFreeSlot? last = dayPieces.Count > 0 ? dayPieces[^1] : null;
                if (last != null && last.End == a && last.BusyUsers.SequenceEqual(busy, StringComparer.OrdinalIgnoreCase))
                    last.End = b; // same people busy, directly after the last piece: one longer window
                else
                    dayPieces.Add(new TeamsFreeSlot { Start = a, End = b, BusyUsers = busy });
            }

            result.Slots.AddRange(dayPieces.Where(s => s.Minutes >= req.DurationMinutes));
        }
        return result;
    }

    /// <summary>Does this event stop the owner from joining a meeting?</summary>
    public static bool Blocks(TeamsCalendarEvent e, bool tentativeIsBusy)
    {
        if (e.IsCancelled) return false;
        if (string.Equals(e.ResponseStatus, "declined", StringComparison.OrdinalIgnoreCase)) return false;
        return e.ShowAs.ToLowerInvariant() switch
        {
            "free" => false,
            "workingelsewhere" => false,
            "tentative" => tentativeIsBusy,
            _ => true, // busy, oof, unknown
        };
    }

    private static List<(DateTime Start, DateTime End)> Clip(IEnumerable<(DateTime Start, DateTime End)> source, DateTime from, DateTime to)
    {
        var list = new List<(DateTime, DateTime)>();
        foreach (var (s, e) in source)
        {
            DateTime cs = s < from ? from : s;
            DateTime ce = e > to ? to : e;
            if (cs < ce) list.Add((cs, ce));
        }
        return list;
    }

    private static bool Overlaps(List<(DateTime Start, DateTime End)> list, DateTime a, DateTime b)
    {
        foreach (var (s, e) in list)
            if (s < b && e > a) return true;
        return false;
    }

    private static DateTime RoundUp(DateTime t, int minutes)
    {
        long ticks = TimeSpan.FromMinutes(minutes).Ticks;
        return new DateTime((t.Ticks + ticks - 1) / ticks * ticks, DateTimeKind.Unspecified);
    }
}
