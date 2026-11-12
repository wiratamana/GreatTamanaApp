
namespace GTA.Tools.Impl.Outlook;

/// <summary>
/// Public, synchronous entry point of the Outlook mail implementation. The tool layer (GTA.Tools) only talks to this class.
/// Every method throws on failure (message = what went wrong); the tool layer turns that into text for the AI.
/// READ ONLY: nothing here sends, moves, deletes or marks mails as read.
/// </summary>
public static class OutlookApi
{
    /// <summary>Most mails one call may return.</summary>
    public const int HardMaxMessages = 100;

    public static void SetLogger(Action<string>? logger) => OutlookSession.SetLogger(logger);

    /// <summary>
    /// Fetches Inbox mails, NEWEST FIRST. Every filter in <paramref name="query"/> is optional and they are combined with AND.
    /// Outlook REST cannot combine free-text search with other filters on the server, so in that case the search runs
    /// on the server and the other filters are applied here, on the newest <see cref="HardMaxMessages"/> search hits.
    /// </summary>
    public static List<EmailMessage> FetchMail(InboxQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        int wanted = Math.Clamp(query.Top, 1, HardMaxMessages);
        bool hasSearch = !string.IsNullOrWhiteSpace(query.SearchText);
        bool hasOtherFilter =
            query.ReceivedAfter.HasValue || query.ReceivedBefore.HasValue ||
            !string.IsNullOrWhiteSpace(query.FromAddress) || !string.IsNullOrWhiteSpace(query.CcAddress) ||
            query.UnreadOnly == true || query.HasAttachmentsOnly == true;

        // Search + other filters: ask the server for the search only (wide window), filter here.
        bool filterLocally = hasSearch && hasOtherFilter;

        var serverQuery = new InboxQuery
        {
            SearchText = query.SearchText,
            Top = filterLocally ? HardMaxMessages : wanted,
            IncludeAttachmentsMetadata = query.IncludeAttachmentsMetadata,
        };
        if (!filterLocally)
        {
            serverQuery.ReceivedAfter = query.ReceivedAfter;
            serverQuery.ReceivedBefore = query.ReceivedBefore;
            serverQuery.FromAddress = query.FromAddress;
            serverQuery.CcAddress = query.CcAddress;
            serverQuery.UnreadOnly = query.UnreadOnly;
            serverQuery.HasAttachmentsOnly = query.HasAttachmentsOnly;
        }

        List<EmailMessage> fetched = Run(c => c.FetchInboxAsync(serverQuery));

        IEnumerable<EmailMessage> result = fetched;
        if (filterLocally)
            result = result.Where(m => MatchesLocally(m, query));

        // The server does not sort when a sender/Cc/unread/attachment filter is used, so always sort here.
        return result
            .OrderByDescending(m => m.ReceivedDateTime ?? DateTimeOffset.MinValue)
            .Take(wanted)
            .ToList();
    }

    private static bool MatchesLocally(EmailMessage m, InboxQuery q)
    {
        if (q.ReceivedAfter.HasValue && !(m.ReceivedDateTime >= q.ReceivedAfter.Value)) return false;
        if (q.ReceivedBefore.HasValue && !(m.ReceivedDateTime <= q.ReceivedBefore.Value)) return false;

        if (!string.IsNullOrWhiteSpace(q.FromAddress) &&
            !string.Equals(m.From?.Address, q.FromAddress.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(q.CcAddress) &&
            !m.Cc.Any(c => string.Equals(c.Address, q.CcAddress.Trim(), StringComparison.OrdinalIgnoreCase)))
            return false;

        if (q.UnreadOnly == true && m.IsRead) return false;
        if (q.HasAttachmentsOnly == true && !m.HasAttachments) return false;
        return true;
    }

    /// <summary>Runs on a thread-pool thread, so it can never deadlock on a UI synchronization context.</summary>
    private static T Run<T>(Func<OutlookMailboxClient, Task<T>> action)
        => Task.Run(() => OutlookSession.RunAsync(action)).GetAwaiter().GetResult();
}
