using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GTA.Tools.Impl.Common;

namespace GTA.Tools
{
    internal static partial class ToolsJiraApi
    {
        private const int JiraSearchDefaultMax = 50;
        private const int JiraSearchHardMax    = 500;   // protects the AI context from huge result lists
        private const int JiraSearchPageSize   = 100;   // max page size used per request
        private const int JiraSearchMaxRetries = 3;     // only for transient errors (network, 5xx, 429, truncated body)

        private static readonly Regex JiraIssueKeyRegex =
            new Regex(@"^[A-Za-z][A-Za-z0-9_]+-\d+$", RegexOptions.Compiled);

        private sealed class JiraSearchPage
        {
            public bool   IsValid   { get; set; }
            public bool   Transient { get; set; }   // true = worth retrying
            public string Error     { get; set; } = "";
            public List<(string Key, string Title, string Date)> Issues { get; } = new List<(string Key, string Title, string Date)>();
            public string? NextPageToken { get; set; }
        }

        [ToolTag("jira_api")]
        [ToolInfo(
            name: "jira_search",
            description:
                "Search the WHOLE JIRA (all projects the account can see) and return matching ticket numbers " +
                "with their last-updated (or created) date and title (summary). Two typical uses: " +
                "(1) KEYWORD search: find which tickets are related to an error code, API name, feature, text, " +
                "or another ticket number (e.g. 'MSEC20191E', 'IFCPLS17O', 'SCOM-14988'). The keyword is searched in " +
                "summary, description, comments and all text custom fields (JQL 'text ~'); if it looks like a " +
                "ticket key, that ticket is included too. " +
                "(2) LIST a project: pass only 'project' (e.g. 'SCONDEF2') with 'sort_by' and 'max_results' to get " +
                "e.g. the 10 most recently updated or created tickets of that project. " +
                "SORTING: 'sort_by' = 'updated' (default, last change), 'created' (creation date) or 'relevance' " +
                "(JIRA's own ranking of the text match, needs a keyword). 'sort_order' = 'desc' (default, newest " +
                "first) or 'asc' (oldest first). " +
                "Output: one line per ticket: 'KEY | date | title' (the date is the 'sort_by' field). " +
                "To read a ticket in full, call jira_read_ticket with the key. " +
                "LIMITS: a keyword is matched as a whole word (and stems), NOT as part of a word: " +
                "'MSEC2019' will not find 'MSEC20191E' — use a wildcard such as 'MSEC2019*' instead. " +
                "A keyword finds tickets that MENTION it; to list the tickets OF a project use 'project', not 'keyword'. " +
                "Attachments are not searched. A very recent edit may take a short time to appear in the index. " +
                "Uses POST /rest/api/3/search/jql with the API-token credentials from init.ini.")]
        [Parameter(index: 0, name: "keyword",
            type: "string",
            description:
                "Optional if 'project' or 'extra_jql' is given. Text to search for. By default the whole text is " +
                "searched as one phrase. Example: 'MSEC20191E' or 'SCOM-14988' or 'login timeout'.",
            required: false)]
        [Parameter(index: 1, name: "project",
            type: "string",
            description:
                "Optional. Limit to these project keys (comma separated), e.g. 'SCONDEF2' or 'SCOM,SCR'. " +
                "Without 'keyword' this lists the tickets of the project. Default: all projects.",
            required: false)]
        [Parameter(index: 2, name: "sort_by",
            type: "string",
            description:
                "Optional. 'updated' (default: last change), 'created' (creation date) or 'relevance' " +
                "(JIRA's text-match ranking; only with a keyword).",
            required: false)]
        [Parameter(index: 3, name: "sort_order",
            type: "string",
            description: "Optional. 'desc' (default: newest first) or 'asc' (oldest first). Ignored for 'relevance'.",
            required: false)]
        [Parameter(index: 4, name: "max_results",
            type: "integer",
            description: "Optional. Maximum number of tickets to return. Default: 50. Maximum: 500. " +
                         "Example: 10 for 'the 10 latest'.",
            required: false)]
        [Parameter(index: 5, name: "all_words",
            type: "boolean",
            description:
                "Optional. If true, split 'keyword' into words and require EVERY word to appear (AND), " +
                "in any order and place. If false (default), the whole keyword is one phrase.",
            required: false)]
        [Parameter(index: 6, name: "extra_jql",
            type: "string",
            description:
                "Optional. An extra JQL filter that is ANDed with the rest, " +
                "e.g. \"status = Open\" or \"created >= -30d\". Do not add ORDER BY (use sort_by).",
            required: false)]
        [Parameter(index: 7, name: "jira_base_url",
            type: "string",
            description: "JIRA instance base URL. Default: the Jira base URL configured in init.ini",
            required: false)]
        [Parameter(index: 8, name: "timeout_ms",
            type: "integer",
            description: "Request timeout in milliseconds, per request. Default: 30000.",
            required: false)]
        public static object JiraSearch(JsonObject input)
        {
            string? keyword   = input["keyword"]?.GetValue<string>()?.Trim();
            string? project   = input["project"]?.GetValue<string>()?.Trim();
            bool    allWords  = input["all_words"]?.GetValue<bool>() ?? false;
            string? extraJql  = input["extra_jql"]?.GetValue<string>()?.Trim();
            string  sortBy    = (input["sort_by"]?.GetValue<string>() ?? "updated").Trim().ToLowerInvariant();
            string  sortOrder = (input["sort_order"]?.GetValue<string>() ?? "desc").Trim().ToLowerInvariant();
            int     maxResult = input["max_results"]?.GetValue<int>() ?? JiraSearchDefaultMax;
            string  baseUrl   = (input["jira_base_url"]?.GetValue<string>()
                                    ?? JiraDefaultBaseUrl).TrimEnd('/');
            int     timeoutMs = input["timeout_ms"]?.GetValue<int>() ?? 30_000;

            if (string.IsNullOrWhiteSpace(keyword) && string.IsNullOrWhiteSpace(project) &&
                string.IsNullOrWhiteSpace(extraJql))
                return "Error: give at least one of 'keyword', 'project' or 'extra_jql'.";
            if (string.IsNullOrWhiteSpace(baseUrl))
                return "Error: no Jira base URL. Set 'jira_base_url' in init.ini or pass 'jira_base_url'.";
            if (sortBy != "updated" && sortBy != "created" && sortBy != "relevance")
                return $"Error: 'sort_by' must be 'updated', 'created' or 'relevance' (got '{sortBy}').";
            if (sortOrder != "desc" && sortOrder != "asc")
                return $"Error: 'sort_order' must be 'desc' or 'asc' (got '{sortOrder}').";

            // "relevance" only means something for a text search.
            string sortNote = "";
            if (sortBy == "relevance" && string.IsNullOrWhiteSpace(keyword))
            {
                sortBy = "updated";
                sortNote = " (no keyword, so 'relevance' was replaced by 'updated')";
            }
            bool   ascending = sortOrder == "asc";
            string dateField = sortBy == "created" ? "created" : "updated";   // date column shown in the output
            string sortLabel = sortBy == "relevance"
                ? "relevance (JIRA's own ranking)"
                : $"{sortBy} {(ascending ? "oldest first" : "newest first")}{sortNote}";

            if (maxResult < 1) maxResult = 1;
            if (maxResult > JiraSearchHardMax) maxResult = JiraSearchHardMax;

            string jql = JiraBuildSearchJql(keyword, allWords, project, extraJql, sortBy, ascending);
            string url = $"{baseUrl}/rest/api/3/search/jql";

            var headers = new Dictionary<string, string>
            {
                { "Accept",       "application/json" },
                { "Content-Type", "application/json" }
            };

            var  hits      = new List<(string Key, string Title, string Date)>();
            var  seenKeys  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? nextToken = null;
            bool moreExist = false;

            while (true)
            {
                int pageSize = Math.Min(JiraSearchPageSize, maxResult - hits.Count);
                if (pageSize <= 0) { moreExist = true; break; }

                var body = new Dictionary<string, object?>
                {
                    ["jql"]        = jql,
                    ["maxResults"] = pageSize,
                    ["fields"]     = new[] { "summary", dateField },   // title + one date: small + fast
                };
                if (nextToken != null) body["nextPageToken"] = nextToken;
                string bodyJson = JsonSerializer.Serialize(body, JsonOptions);

                var (_, page, lastError) = ToolsCommonPipe.RetryUntilValid(
                    attempt:         () => JiraTryFetchSearchPage(url, bodyJson, headers, timeoutMs, dateField),
                    isValid:         p => p.IsValid || !p.Transient,   // permanent errors (e.g. bad JQL) are not retried
                    describeInvalid: p => p.Error,
                    maxRetries:      JiraSearchMaxRetries);

                if (page == null || !page.IsValid)
                    return $"Error: JIRA search failed — {(page?.Error ?? lastError)}\nJQL used: {jql}";

                foreach (var issue in page.Issues)
                    if (seenKeys.Add(issue.Key))
                        hits.Add(issue);

                nextToken = page.NextPageToken;
                if (string.IsNullOrEmpty(nextToken)) break;          // last page
                if (hits.Count >= maxResult) { moreExist = true; break; } // cap reached, JIRA has more
            }

            return JiraFormatSearchResult(keyword, project, extraJql, sortLabel, jql, hits, moreExist, maxResult);
        }

        // Escape text for use inside a JQL double-quoted string.
        private static string JiraJqlQuote(string s) =>
            "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        // keyword may be null: then only project / extra_jql restrict the search (e.g. "all tickets of project X").
        // orderBy: "updated" | "created" | "relevance". "relevance" = no ORDER BY clause (JIRA ranks text matches itself).
        private static string JiraBuildSearchJql(
            string? keyword, bool allWords, string? project, string? extraJql, string orderBy, bool ascending)
        {
            var clauses = new List<string>();

            if (!string.IsNullOrWhiteSpace(keyword))
            {
                string textClause;
                if (allWords)
                {
                    var words = keyword!.Split(new[] { ' ', '\t', '\u3000' }, StringSplitOptions.RemoveEmptyEntries);
                    textClause = string.Join(" AND ", words.Select(w => $"text ~ {JiraJqlQuote(w)}"));
                }
                else
                {
                    textClause = $"text ~ {JiraJqlQuote(keyword!)}";
                }

                // A ticket number (ABC-123): also match the ticket itself, not only tickets that mention it.
                if (JiraIssueKeyRegex.IsMatch(keyword!))
                    clauses.Add($"(key = {keyword!.ToUpperInvariant()} OR {textClause})");
                else
                    clauses.Add($"({textClause})");
            }

            List<string> projects = JiraSplitList(project);
            if (projects.Count > 0)
                clauses.Add($"project in ({string.Join(", ", projects.Select(JiraJqlQuote))})");

            if (!string.IsNullOrWhiteSpace(extraJql))
                clauses.Add($"({extraJql})");

            string jql = string.Join(" AND ", clauses);
            if (orderBy != "relevance")
                jql += $" ORDER BY {orderBy} {(ascending ? "ASC" : "DESC")}";
            return jql;
        }

        // One request = one page. Never throws; returns IsValid/Transient/Error.
        private static JiraSearchPage JiraTryFetchSearchPage(
            string url, string bodyJson, Dictionary<string, string> headers, int timeoutMs, string dateField)
        {
            var page = new JiraSearchPage();

            // SendCommand (not JiraGetResponseBody) so the HTTP status line of an error is kept.
            string formatted = SendCommand(new
            {
                command = "RestApiCall", url, httpMethod = "POST",
                body = bodyJson, headers, timeoutMs
            });

            const string marker = "--- API Response Body ---";
            int markerIdx = formatted.IndexOf(marker, StringComparison.Ordinal);
            string responseBody = markerIdx >= 0
                ? formatted.Substring(markerIdx + marker.Length).TrimStart('\r', '\n').TrimEnd()
                : "";

            if (formatted.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
            {
                string firstLine = formatted.Split('\n')[0].Trim();
                if (firstLine.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
                    firstLine = firstLine.Substring("Error:".Length).Trim();   // the caller adds its own "Error:" prefix
                page.Error = responseBody.Length > 0
                    ? $"{firstLine} {JiraSearchShorten(responseBody, 600)}"
                    : firstLine;
                page.Transient = ToolsCommonPipe.IsNetErr(formatted)
                                 || Regex.IsMatch(firstLine, @"HTTP (5\d\d|429)");
                return page;
            }

            if (responseBody.Length == 0)
            {
                page.Error = "JIRA returned an empty response body.";
                page.Transient = true;
                return page;
            }

            try
            {
                using var doc = JsonDocument.Parse(responseBody);
                JsonElement root = doc.RootElement;

                if (!root.TryGetProperty("issues", out var issues) || issues.ValueKind != JsonValueKind.Array)
                {
                    // JIRA error JSON, e.g. {"errorMessages":["..."]}: permanent, do not retry
                    if (root.TryGetProperty("errorMessages", out var em) && em.ValueKind == JsonValueKind.Array &&
                        em.GetArrayLength() > 0)
                    {
                        page.Error = string.Join(" | ", em.EnumerateArray().Select(e => e.GetString()));
                        return page;
                    }

                    page.Error = $"Response has no valid 'issues' array ({responseBody.Length} char(s)) — " +
                                 "likely truncated by a flaky proxy/internet connection.";
                    page.Transient = true;
                    return page;
                }

                foreach (JsonElement issue in issues.EnumerateArray())
                {
                    string key = issue.TryGetProperty("key", out var kEl) ? (kEl.GetString() ?? "") : "";
                    if (key.Length == 0) continue;

                    string title = "";
                    string date  = "";
                    if (issue.TryGetProperty("fields", out var f) && f.ValueKind == JsonValueKind.Object)
                    {
                        if (f.TryGetProperty("summary", out var sEl) && sEl.ValueKind == JsonValueKind.String)
                            title = sEl.GetString() ?? "";

                        if (f.TryGetProperty(dateField, out var dEl) && dEl.ValueKind == JsonValueKind.String)
                        {
                            // "2026-11-09T18:45:42.530+0900" -> "2026-11-09 18:45" (time zone = the account's JIRA setting)
                            string raw = dEl.GetString() ?? "";
                            date = raw.Length >= 16 ? raw.Substring(0, 16).Replace('T', ' ') : raw;
                        }
                    }

                    page.Issues.Add((key, title, date));
                }

                page.NextPageToken =
                    root.TryGetProperty("nextPageToken", out var tEl) && tEl.ValueKind == JsonValueKind.String
                        ? tEl.GetString()
                        : null;
                page.IsValid = true;
                return page;
            }
            catch (JsonException ex)
            {
                page.Error = $"Failed to parse JIRA response — {ex.Message}";
                page.Transient = true;   // typically a truncated body
                return page;
            }
        }

        private static string JiraSearchShorten(string s, int max) =>
            s.Length <= max ? s : s.Substring(0, max) + "...";

        private static string JiraFormatSearchResult(
            string? keyword, string? project, string? extraJql, string sortLabel, string jql,
            List<(string Key, string Title, string Date)> hits, bool moreExist, int maxResult)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(keyword))  parts.Add($"keyword: \"{keyword}\"");
            parts.Add(string.IsNullOrWhiteSpace(project) ? "all projects" : $"project: {project}");
            if (!string.IsNullOrWhiteSpace(extraJql)) parts.Add($"filter: {extraJql}");
            parts.Add($"sorted by: {sortLabel}");

            var sb = new StringBuilder();
            sb.AppendLine("JIRA search — " + string.Join(" | ", parts));
            sb.AppendLine($"JQL: {jql}");

            if (hits.Count == 0)
            {
                sb.AppendLine("No tickets found.");
                sb.AppendLine(string.IsNullOrWhiteSpace(keyword)
                    ? "Hint: check the project key and the filter, and that the account can see the project."
                    : "Hint: JIRA text search matches whole words only. Try a wildcard (e.g. 'MSEC2019*'), " +
                      "fewer or different words, or check that the account can see the project.");
                return sb.ToString().TrimEnd();
            }

            sb.AppendLine(moreExist
                ? $"Found {hits.Count} ticket(s) — stopped at max_results={maxResult}, MORE EXIST " +
                  "(raise max_results, or narrow with 'project' / 'extra_jql'). Format: KEY | date | title:"
                : $"Found {hits.Count} ticket(s). Format: KEY | date | title:");
            sb.AppendLine();

            foreach (var (key, title, date) in hits)
            {
                string cleanTitle = Regex.Replace(title, @"\s+", " ").Trim();
                sb.AppendLine($"{key} | {date} | {cleanTitle}");
            }

            return sb.ToString().TrimEnd();
        }
    }
}
