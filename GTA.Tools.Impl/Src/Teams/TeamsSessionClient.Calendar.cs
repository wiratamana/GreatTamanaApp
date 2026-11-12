
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace GTA.Tools.Impl.Teams;

// Calendar part of the client: read the calendar of the signed-in user or of OTHER people (Microsoft Graph, read only).
//
// How it works (proven with the Teams3_Copy smoke test against the real tenant):
//   * Same sign-in, same refresh_token, same client id as every other Graph call. Only the SCOPE is different:
//     Calendars.Read + Calendars.Read.Shared. The ".default" Graph token of the user lookup is not used here,
//     because the explicit scopes are the ones that were proven to work.
//   * GET /users/{mail}/calendarView?startDateTime=..&endDateTime=.. returns every event in the range
//     (recurring series already expanded into single events). In this tenant the calendars of other people are readable,
//     with subject, time, location and organizer.
//   * Graph returns ONE PAGE (default 10 events). You MUST follow @odata.nextLink. The old smoke test did not, and
//     silently lost everything after the first 250 events (looked like "free" time). This code always follows it,
//     and marks the result as Truncated when it had to stop early.
//   * Times come back as wall-clock time in this PC's time zone (Prefer: outlook.timezone), so there is no
//     UTC conversion to get wrong.
public sealed partial class TeamsSessionClient
{
    private const string CalendarScope =
        "https://graph.microsoft.com/Calendars.Read.Shared https://graph.microsoft.com/Calendars.Read offline_access";

    private const string CalendarSelect = "subject,start,end,isAllDay,isCancelled,showAs,location,organizer,responseStatus,sensitivity";

    private const int CalendarPageSize = 250;

    /// <summary>Safety stop while paging (250 events per page).</summary>
    private const int MaxCalendarPages = 40;

    /// <summary>How many calendars are read at the same time.</summary>
    private const int CalendarParallel = 4;

    private string? _calendarAccessToken;

    #region Calendar token

    private async Task<string> GetCalendarAccessTokenAsync()
    {
        if (!string.IsNullOrEmpty(_calendarAccessToken) && GetJwtExpiryUtc(_calendarAccessToken) > DateTime.UtcNow.AddMinutes(2))
            return _calendarAccessToken;

        EnsureBootstrapped();
        if (string.IsNullOrEmpty(_refreshToken))
            throw new InvalidOperationException(
                "No refresh_token is available, so a calendar token cannot be created. " +
                "Delete the cached Teams session file and sign in again (full login).");

        var fields = new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["scope"] = CalendarScope,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = _refreshToken!,
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://login.microsoftonline.com/common/oauth2/v2.0/token")
        {
            Content = new FormUrlEncodedContent(fields),
        };
        req.Headers.Referrer = new Uri("https://teams.microsoft.com/");
        req.Headers.Add("Origin", "https://teams.microsoft.com");
        using var resp = await _http.SendAsync(req);
        string body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Calendar token request failed (status {(int)resp.StatusCode}). " +
                $"The scopes Calendars.Read / Calendars.Read.Shared are probably not allowed for this account. Body: {body}");

        using var doc = JsonDocument.Parse(body);
        _calendarAccessToken = doc.RootElement.GetProperty("access_token").GetString()!;
        Log("Acquired calendar-scoped Graph access_token (Calendars.Read, Calendars.Read.Shared).");
        return _calendarAccessToken;
    }

    #endregion

    #region Read calendars

    /// <summary>
    /// Reads the calendars of several people for [fromUtc, toUtc). A person that cannot be read (no permission,
    /// unknown mailbox, ...) does NOT stop the others: it comes back with <see cref="TeamsCalendarResult.Error"/> set.
    /// An HTTP 401 is thrown, so the session layer can renew the token and try again.
    /// </summary>
    public async Task<List<TeamsCalendarResult>> ReadCalendarsAsync(IReadOnlyList<string> users,
        DateTimeOffset fromUtc, DateTimeOffset toUtc, int maxEventsPerUser)
    {
        EnsureBootstrapped();
        string token = await GetCalendarAccessTokenAsync();

        var results = new TeamsCalendarResult[users.Count];
        using var gate = new SemaphoreSlim(CalendarParallel);
        var tasks = users.Select(async (user, index) =>
        {
            await gate.WaitAsync();
            try
            {
                results[index] = await ReadOneCalendarAsync(token, user, fromUtc, toUtc, maxEventsPerUser);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();
        await Task.WhenAll(tasks);
        return results.ToList();
    }

    private async Task<TeamsCalendarResult> ReadOneCalendarAsync(string token, string user,
        DateTimeOffset fromUtc, DateTimeOffset toUtc, int maxEvents)
    {
        var result = new TeamsCalendarResult { User = user };
        string owner = string.Equals(user, "me", StringComparison.OrdinalIgnoreCase)
            ? "me"
            : $"users/{Uri.EscapeDataString(user)}";
        string start = fromUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        string end = toUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

        string? url = $"{GraphBase}/{owner}/calendarView" +
                      $"?startDateTime={Uri.EscapeDataString(start)}&endDateTime={Uri.EscapeDataString(end)}" +
                      $"&$select={CalendarSelect}&$orderby=start%2fdateTime&$top={CalendarPageSize}";
        int pages = 0;
        try
        {
            while (url != null)
            {
                if (++pages > MaxCalendarPages) { result.Truncated = true; break; }

                string body = await SendCalendarRequestAsync(token, url);
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("value", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var raw in arr.EnumerateArray())
                    {
                        if (result.Events.Count >= maxEvents) { result.Truncated = true; break; }
                        TeamsCalendarEvent? ev = ParseCalendarEvent(raw);
                        if (ev != null) result.Events.Add(ev);
                    }
                }
                if (result.Truncated) break;

                url = doc.RootElement.TryGetProperty("@odata.nextLink", out var next) ? next.GetString() : null;
                if (string.IsNullOrEmpty(url)) url = null;
            }
        }
        catch (InvalidOperationException ex) when (!ex.Message.Contains("(status 401", StringComparison.Ordinal))
        {
            result.Error = ex.Message; // this person only; the others are still read
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            result.Error = $"{ex.GetType().Name}: {ex.Message}";
        }
        return result;
    }

    /// <summary>One GET with the calendar token. Retries on 429 / 503 / 504 (throttling). Other failures throw a SHORT message.</summary>
    private async Task<string> SendCalendarRequestAsync(string token, string url)
    {
        const int maxAttempts = 4;
        for (int attempt = 1; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Add("Accept", "application/json");
            // Times come back in this PC's time zone. TimeZoneInfo.Local.Id is a Windows id ("Tokyo Standard Time"), which Graph accepts.
            req.Headers.Add("Prefer", $"outlook.timezone=\"{TimeZoneInfo.Local.Id}\"");

            using var resp = await _http.SendAsync(req);
            string body = await resp.Content.ReadAsStringAsync();
            if (resp.IsSuccessStatusCode) return body;

            int status = (int)resp.StatusCode;
            if ((status == 429 || status == 503 || status == 504) && attempt < maxAttempts)
            {
                TimeSpan wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2 * attempt);
                if (wait > TimeSpan.FromSeconds(20)) wait = TimeSpan.FromSeconds(20);
                Log($"Calendar request got HTTP {status}; waiting {wait.TotalSeconds:0}s before attempt {attempt + 1}.");
                await Task.Delay(wait);
                continue;
            }

            throw new InvalidOperationException($"Calendar request failed (status {status}): {DescribeCalendarError(status, body)}");
        }
    }

    private static string DescribeCalendarError(int status, string body)
    {
        string code = "", message = "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
            {
                code = Str(err, "code");
                message = Str(err, "message");
            }
        }
        catch (JsonException)
        {
            // not JSON: use the raw text below
        }
        if (code.Length == 0 && message.Length == 0)
            message = body.Length > 300 ? body[..300] + "…" : body;

        string hint = status switch
        {
            403 => "No permission: this calendar is not shared with you (or the app may not read it). ",
            404 => "User or mailbox not found. Check the e-mail address (teams_fetch_user_name can look it up). ",
            401 => "Token not accepted. ",
            _ => "",
        };
        return $"{hint}{code} {message}".Trim();
    }

    /// <summary>
    /// Graph sends JSON null for missing objects (for example "organizer": null on some events), so every child object
    /// is checked with <see cref="Obj"/> first. An event whose TIME cannot be read throws: dropping it silently would show
    /// that person as free.
    /// </summary>
    private static TeamsCalendarEvent ParseCalendarEvent(JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("The calendar answer contains an event in an unknown format.");
        if (!Obj(raw, "start", out var startEl) || !Obj(raw, "end", out var endEl))
            throw new InvalidOperationException($"An event without start/end time was returned (subject \"{Str(raw, "subject")}\").");

        // "2026-11-12T09:00:00.0000000" (no offset: wall-clock time of the requested time zone).
        if (!DateTime.TryParse(Str(startEl, "dateTime"), CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime start) ||
            !DateTime.TryParse(Str(endEl, "dateTime"), CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime end))
            throw new InvalidOperationException($"An event with an unreadable time was returned (subject \"{Str(raw, "subject")}\").");

        bool Flag(string name) => raw.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

        string showAs = Str(raw, "showAs");
        string response = Obj(raw, "responseStatus", out var rs) ? Str(rs, "response") : "";
        string organizer = "";
        if (Obj(raw, "organizer", out var org) && Obj(org, "emailAddress", out var orgMail))
        {
            string name = Str(orgMail, "name");
            organizer = name.Length > 0 ? name : Str(orgMail, "address");
        }

        return new TeamsCalendarEvent
        {
            Subject = Str(raw, "subject"),
            Start = start,
            End = end,
            IsAllDay = Flag("isAllDay"),
            IsCancelled = Flag("isCancelled"),
            ShowAs = showAs.Length > 0 ? showAs : "busy",
            ResponseStatus = response,
            Location = Obj(raw, "location", out var loc) ? Str(loc, "displayName") : "",
            Organizer = organizer,
            Sensitivity = Str(raw, "sensitivity"),
        };
    }

    /// <summary>True only when the property exists AND is a JSON object (not null, not a string).</summary>
    private static bool Obj(JsonElement parent, string name, out JsonElement child)
    {
        if (parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out child) && child.ValueKind == JsonValueKind.Object)
            return true;
        child = default;
        return false;
    }

    #endregion
}
