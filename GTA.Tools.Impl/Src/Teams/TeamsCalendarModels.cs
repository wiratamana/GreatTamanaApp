
namespace GTA.Tools.Impl.Teams;

/// <summary>
/// One calendar event (an instance of a recurring series is one event each).
/// All times are WALL-CLOCK times in this PC's time zone (Graph is asked for them with the Prefer header).
/// An all-day event runs from 00:00 of its first day to 00:00 of the day after its last day.
/// </summary>
public sealed class TeamsCalendarEvent
{
    public string Subject { get; set; } = "";
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public bool IsAllDay { get; set; }
    public bool IsCancelled { get; set; }

    /// <summary>"free", "tentative", "busy", "oof", "workingElsewhere" or "unknown".</summary>
    public string ShowAs { get; set; } = "busy";

    /// <summary>Answer of the calendar OWNER: "none", "organizer", "accepted", "tentativelyAccepted", "declined", "notResponded".</summary>
    public string ResponseStatus { get; set; } = "";

    public string Location { get; set; } = "";
    public string Organizer { get; set; } = "";

    /// <summary>"normal", "personal", "private" or "confidential".</summary>
    public string Sensitivity { get; set; } = "";
}

/// <summary>The calendar of ONE person for a date range. Either <see cref="Events"/> is valid or <see cref="Error"/> is set.</summary>
public sealed class TeamsCalendarResult
{
    /// <summary>The e-mail / user principal name that was asked for (or "me").</summary>
    public string User { get; set; } = "";

    public List<TeamsCalendarEvent> Events { get; } = new();

    /// <summary>Null = the calendar was read. Otherwise the short reason (for example "no permission").</summary>
    public string? Error { get; set; }

    /// <summary>True = there were more events than we were allowed to read. The list is NOT complete.</summary>
    public bool Truncated { get; set; }
}

/// <summary>Input of the common-free-time search.</summary>
public sealed class TeamsFreeTimeRequest
{
    /// <summary>E-mail / user principal name of every attendee. "me" = the signed-in user.</summary>
    public List<string> Users { get; set; } = new();

    /// <summary>First day to search (date part only, this PC's time zone).</summary>
    public DateTime FromDate { get; set; }

    /// <summary>Last day to search, INCLUDED (date part only).</summary>
    public DateTime ToDate { get; set; }

    public TimeSpan WorkStart { get; set; } = TimeSpan.FromHours(9);
    public TimeSpan WorkEnd { get; set; } = TimeSpan.FromHours(18);

    /// <summary>A free window must be at least this long (the meeting length).</summary>
    public int DurationMinutes { get; set; } = 30;

    /// <summary>How many attendees may be busy in a reported window. 0 = everybody must be free.</summary>
    public int MaxUnavailable { get; set; }

    /// <summary>true = a "tentative" event blocks the time (safe choice). false = tentative counts as free.</summary>
    public bool TentativeIsBusy { get; set; } = true;

    public bool SkipJapaneseHolidays { get; set; } = true;

    /// <summary>Extra days that are never offered (company holidays, and so on). Date part only.</summary>
    public List<DateTime> SkipDates { get; set; } = new();

    /// <summary>Time ranges that are blocked EVERY day for everybody (for example lunch 12:00-13:00).</summary>
    public List<(TimeSpan Start, TimeSpan End)> DailyBlocked { get; set; } = new();

    /// <summary>true = attendees whose calendar cannot be read are left out. false (default) = the search is refused.</summary>
    public bool IgnoreUnreadable { get; set; }
}

/// <summary>One window of free time (this PC's time zone).</summary>
public sealed class TeamsFreeSlot
{
    public DateTime Start { get; set; }
    public DateTime End { get; set; }

    /// <summary>Attendees who are busy in this window. Empty when everybody is free.</summary>
    public List<string> BusyUsers { get; set; } = new();

    public int Minutes => (int)(End - Start).TotalMinutes;
}

public sealed class TeamsFreeTimeResult
{
    /// <summary>Attendees whose calendar was read and used.</summary>
    public List<string> CheckedUsers { get; } = new();

    /// <summary>Attendees whose calendar could not be read (only possible with IgnoreUnreadable).</summary>
    public List<TeamsCalendarResult> Unreadable { get; } = new();

    public List<TeamsFreeSlot> Slots { get; } = new();

    /// <summary>Working days that were skipped on purpose, with the reason (holiday name, or "skip_dates"). Weekends are not listed.</summary>
    public SortedDictionary<DateTime, string> SkippedDays { get; } = new();

    /// <summary>Number of calendar events that block time, per attendee.</summary>
    public Dictionary<string, int> BlockingEventCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
}
