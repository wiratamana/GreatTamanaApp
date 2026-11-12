
namespace GTA.Tools.Impl.Teams;

/// <summary>
/// Public, synchronous entry point of the Teams implementation. The tool layer (GTA.Tools) only talks to this class.
/// Every method throws on failure (message = what went wrong); the tool layer turns that into text for the AI.
/// </summary>
public static class TeamsApi
{
    /// <summary>Most files one message may carry.</summary>
    public const int MaxAttachments = 5;

    /// <summary>Biggest file we upload (the AI must not push huge data out of this PC).</summary>
    public const long MaxAttachmentBytes = 100L * 1024 * 1024;

    /// <summary>File names that hold secrets in this project. They are never sent.</summary>
    private static readonly string[] BlockedFileNames = { "init.ini", "secret.ini", "credentials.ini", "my_credentials.dat" };

    private static readonly string[] BlockedExtensions = { ".key", ".pfx", ".p12", ".pem" };

    /// <summary>App folders that hold secrets and session tokens. Nothing inside is ever sent.</summary>
    private static readonly string[] BlockedFolders = { "Config", "Credentials" };

    public static void SetLogger(Action<string>? logger) => TeamsSession.SetLogger(logger);

    /// <summary>All rooms (id, name, kind) the signed-in user can see. "Notes to self" is first.</summary>
    public static List<TeamsRoom> FetchRooms(int maxRooms = 500)
        => Run(c => c.ListRoomsAsync(maxRooms));

    /// <summary>Newest messages of one room inside the range, oldest first. Null = open end.</summary>
    public static List<TeamsMessage> ReadMessages(string roomId, DateTimeOffset? fromUtc, DateTimeOffset? toUtc, int maxMessages)
    {
        if (string.IsNullOrWhiteSpace(roomId)) throw new ArgumentException("room_id is required.");
        return Run(c => c.ReadMessagesAsync(roomId.Trim(), fromUtc, toUtc, Math.Max(1, maxMessages)));
    }

    /// <summary>Hard limit for one notification fetch.</summary>
    public const int HardMaxNotifications = 200;

    /// <summary>
    /// Activity feed (mentions, replies, reactions, meeting updates), newest activity first. READ ONLY: it never marks
    /// anything as read. All filters are optional. 'types' are activity types such as "mention" or "replyToReply".
    /// </summary>
    public static List<TeamsNotification> FetchNotifications(int maxItems, DateTimeOffset? sinceUtc, bool unreadOnly,
        IReadOnlyCollection<string>? types)
    {
        int max = Math.Clamp(maxItems, 1, HardMaxNotifications);
        return Run(c => c.ReadNotificationsAsync(max, sinceUtc, unreadOnly, types));
    }

    /// <summary>Sends a message (and optional local files) to a room. This CHANGES data in Teams.</summary>
    public static TeamsPostResult PostMessage(string roomId, string text, IReadOnlyList<string> filePaths,
        TeamsTextFormat format = TeamsTextFormat.Text)
    {
        if (string.IsNullOrWhiteSpace(roomId)) throw new ArgumentException("room_id is required.");
        if (string.IsNullOrWhiteSpace(text) && filePaths.Count == 0)
            throw new ArgumentException("Give a text or at least one file.");
        if (filePaths.Count > MaxAttachments)
            throw new ArgumentException($"At most {MaxAttachments} files per message (got {filePaths.Count}).");

        // Check every file BEFORE anything is uploaded or sent.
        var fullPaths = new List<string>();
        foreach (string path in filePaths)
            fullPaths.Add(CheckAttachment(path));

        return Run(c => c.PostMessageAsync(roomId.Trim(), text ?? "", fullPaths, format));
    }

    /// <summary>
    /// Replaces the text of one of the user's OWN messages (text messages only, no file attachments).
    /// This CHANGES data in Teams: the message shows "edited" to everybody in the room.
    /// </summary>
    public static TeamsEditResult EditMessage(string roomId, string messageId, string text,
        TeamsTextFormat format = TeamsTextFormat.Text)
    {
        if (string.IsNullOrWhiteSpace(roomId)) throw new ArgumentException("room_id is required.");
        if (string.IsNullOrWhiteSpace(messageId)) throw new ArgumentException("message_id is required.");
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("The new text is required (an edit cannot make a message empty).");

        return Run(c => c.EditMessageAsync(roomId.Trim(), messageId.Trim(), text, format));
    }

    /// <summary>One user by object id, "8:orgid:{id}" or user principal name. Null if not found.</summary>
    public static TeamsUser? FetchUser(string idOrMriOrUpn)
    {
        if (string.IsNullOrWhiteSpace(idOrMriOrUpn)) throw new ArgumentException("A user id is required.");
        return Run(c => c.GetUserAsync(idOrMriOrUpn));
    }

    /// <summary>Directory search by display name, mail or user principal name.</summary>
    public static List<TeamsUser> SearchUsers(string query, int top = 10)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("A search text is required.");
        return Run(c => c.SearchUsersAsync(query, top));
    }

    /// <summary>Most people one calendar / free-time call may read.</summary>
    public const int HardMaxCalendarUsers = 20;

    /// <summary>Most events read per person in one calendar call.</summary>
    public const int HardMaxCalendarEvents = 500;

    /// <summary>Longest date range of one calendar / free-time call.</summary>
    public const int HardMaxCalendarDays = 62;

    /// <summary>Events per person that a free-time search may read. If a calendar has more, the search is refused (it would be wrong).</summary>
    private const int FreeTimeMaxEventsPerUser = 2000;

    /// <summary>
    /// Calendar events of one or more people (READ ONLY) for [fromUtc, toUtc). "me" = the signed-in user.
    /// A person that cannot be read comes back with Error set; the others are still returned, in the same order as asked.
    /// </summary>
    public static List<TeamsCalendarResult> FetchCalendars(IReadOnlyList<string> users, DateTimeOffset fromUtc, DateTimeOffset toUtc,
        int maxEventsPerUser)
    {
        if (users.Count == 0) throw new ArgumentException("At least one user is required.");
        if (users.Count > HardMaxCalendarUsers) throw new ArgumentException($"At most {HardMaxCalendarUsers} users per call (got {users.Count}).");
        if (toUtc <= fromUtc) throw new ArgumentException("The end of the range must be after the start.");
        if (toUtc - fromUtc > TimeSpan.FromDays(HardMaxCalendarDays + 1))
            throw new ArgumentException($"The date range is too long (limit {HardMaxCalendarDays} days).");

        int max = Math.Clamp(maxEventsPerUser, 1, HardMaxCalendarEvents);
        return Run(c => c.ReadCalendarsAsync(users, fromUtc, toUtc, max));
    }

    /// <summary>
    /// Windows in which everybody (or all but <see cref="TeamsFreeTimeRequest.MaxUnavailable"/> people) is free. READ ONLY.
    /// Throws when a calendar cannot be read or is too big to read completely (unless IgnoreUnreadable), because a
    /// missing calendar would look like "free all day" and give a wrong answer.
    /// </summary>
    public static TeamsFreeTimeResult FindFreeTime(TeamsFreeTimeRequest req)
    {
        if (req.Users.Count == 0) throw new ArgumentException("At least one attendee is required.");
        if (req.Users.Count > HardMaxCalendarUsers) throw new ArgumentException($"At most {HardMaxCalendarUsers} attendees per call (got {req.Users.Count}).");
        if (req.ToDate.Date < req.FromDate.Date) throw new ArgumentException("'date_to' is before 'date_from'.");
        if ((req.ToDate.Date - req.FromDate.Date).TotalDays + 1 > HardMaxCalendarDays)
            throw new ArgumentException($"The date range is too long (limit {HardMaxCalendarDays} days).");
        if (req.WorkEnd <= req.WorkStart) throw new ArgumentException("The end of the work hours must be after the start.");
        if (req.DurationMinutes < 5) throw new ArgumentException("The meeting length must be at least 5 minutes.");
        if (req.MaxUnavailable < 0) throw new ArgumentException("max_unavailable cannot be negative.");

        // Read from the start of the first day to the end of the last day (this PC's time zone).
        DateTimeOffset fromUtc = new DateTimeOffset(DateTime.SpecifyKind(req.FromDate.Date, DateTimeKind.Unspecified)).ToUniversalTime();
        DateTimeOffset toUtc = new DateTimeOffset(DateTime.SpecifyKind(req.ToDate.Date.AddDays(1), DateTimeKind.Unspecified)).ToUniversalTime();

        List<TeamsCalendarResult> calendars = Run(c => c.ReadCalendarsAsync(req.Users, fromUtc, toUtc, FreeTimeMaxEventsPerUser));

        // Incomplete data = wrong answer. A calendar that failed or was cut short is a problem, not "free".
        var problems = calendars.Where(c => c.Error != null || c.Truncated).ToList();
        if (problems.Count > 0 && !req.IgnoreUnreadable)
        {
            string list = string.Join("; ", problems.Select(p =>
                $"{p.User}: {(p.Error?.TrimEnd('.', ' ') ?? $"more than {FreeTimeMaxEventsPerUser} events, calendar was cut short (use a shorter date range)")}"));
            throw new InvalidOperationException(
                $"Cannot read the full calendar of {problems.Count} attendee(s): {list}. " +
                "No free time is calculated, because a missing calendar would look like free time. " +
                "Fix the address / permission, or set ignore_unreadable=true to calculate without these people.");
        }

        List<TeamsCalendarResult> readable = calendars.Where(c => c.Error == null && !c.Truncated).ToList();
        if (readable.Count == 0)
            throw new InvalidOperationException("No calendar could be read, so there is nothing to calculate.");

        TeamsFreeTimeResult result = TeamsFreeTime.Compute(req, readable, DateTime.Now);
        result.Unreadable.AddRange(problems);
        return result;
    }

    /// <summary>Returns the full path of a file that is allowed to leave this PC, or throws.</summary>
    private static string CheckAttachment(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("An attachment path is empty.");

        string full = Path.GetFullPath(path.Trim());
        if (!File.Exists(full)) throw new FileNotFoundException($"Attachment not found: \"{full}\".", full);

        long size = new FileInfo(full).Length;
        if (size == 0) throw new InvalidOperationException($"Attachment is empty: \"{full}\".");
        if (size > MaxAttachmentBytes)
            throw new InvalidOperationException($"Attachment is too big ({size / 1024 / 1024} MB, limit {MaxAttachmentBytes / 1024 / 1024} MB): \"{full}\".");

        string name = Path.GetFileName(full);
        string ext = Path.GetExtension(full);
        if (BlockedFileNames.Contains(name, StringComparer.OrdinalIgnoreCase) ||
            BlockedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Refused: \"{name}\" is a secret/key file and is never sent.");

        foreach (string folder in BlockedFolders)
        {
            string blocked = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, folder)) + Path.DirectorySeparatorChar;
            if (full.StartsWith(blocked, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Refused: files inside the app's \"{folder}\" folder are never sent.");
        }

        return full;
    }

    /// <summary>Runs on a thread-pool thread, so it can never deadlock on a UI synchronization context.</summary>
    private static T Run<T>(Func<TeamsSessionClient, Task<T>> action)
        => Task.Run(() => TeamsSession.RunAsync(action)).GetAwaiter().GetResult();
}
