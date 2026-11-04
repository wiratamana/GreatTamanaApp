using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GTA.Tools.Impl.Common;

namespace GTA.Tools
{
    internal static partial class ToolsJiraApi
    {
        // Standard Jira fields that are ALWAYS requested. Custom fields (ids) are NOT in the source code:
        // they are configured in Config\init.ini (see JiraBuildFieldPlan).
        private static readonly string[] JiraCoreFields =
        {
            "summary", "description", "status", "assignee", "reporter",
            "comment", "issuelinks", "priority", "labels", "attachment"
        };

        private const int JiraTicketMaxRetries = 10;
        private const int JiraMaxProfiles      = 50;

        private static List<string> JiraSplitList(string? csv) =>
            (csv ?? "")
                .Split(',')
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToList();

        // Builds the field plan for one project key from Config\init.ini:
        //   jira_common_fields                 = "id1,id2"   requested for every project
        //   jira_profile_<n>_projects          = "KEY1,KEY2" project keys that use profile n (n = 1..50)
        //   jira_profile_<n>_title             = "TITLE"     section title printed for profile n
        //   jira_profile_<n>_fields            = "id1,id2"   field ids requested for profile n
        //   jira_profile_default_title/_fields = used when no profile matches the project key
        private static (List<string> Fields, List<(string Title, List<string> Ids)> Sections)
            JiraBuildFieldPlan(string projectKey)
        {
            var sections = new List<(string Title, List<string> Ids)>();

            List<string> common = JiraSplitList(GTA.Core.InitSettings.Get("jira_common_fields"));
            if (common.Count > 0)
                sections.Add(("COMMON FIELDS", common));

            string title = "";
            string fieldsCsv = "";
            bool matched = false;
            for (int n = 1; n <= JiraMaxProfiles && !matched; n++)
            {
                List<string> projects = JiraSplitList(GTA.Core.InitSettings.Get($"jira_profile_{n}_projects"));
                if (!projects.Contains(projectKey, StringComparer.OrdinalIgnoreCase))
                    continue;

                title     = GTA.Core.InitSettings.Get($"jira_profile_{n}_title");
                fieldsCsv = GTA.Core.InitSettings.Get($"jira_profile_{n}_fields");
                if (string.IsNullOrWhiteSpace(title)) title = $"PROFILE {n} FIELDS";
                matched = true;
            }

            if (!matched)
            {
                title     = GTA.Core.InitSettings.Get("jira_profile_default_title");
                fieldsCsv = GTA.Core.InitSettings.Get("jira_profile_default_fields");
                if (string.IsNullOrWhiteSpace(title)) title = "DEFAULT FIELDS";
            }

            List<string> profileIds = JiraSplitList(fieldsCsv)
                .Where(id => !common.Contains(id, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (profileIds.Count > 0)
                sections.Add((title, profileIds));

            var all = new List<string>(JiraCoreFields);
            foreach (var (_, ids) in sections)
                all.AddRange(ids);

            return (all.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), sections);
        }

        [ToolTag("jira_api")]
        [ToolInfo(
            name: "jira_read_ticket",
            description:
                "Read a JIRA issue/ticket by its key using the JIRA REST API " +
                "(POST /rest/api/3/search/jql). " +
                "Returns a structured, human-readable summary of the issue including: " +
                "key, summary, status, priority, assignee, reporter, labels, " +
                "plain-text description (ADF converted), attachments (id, filename, type, size, download URL), " +
                "linked issues, and all comments (author, timestamp, and plain-text body). " +
                "The extra custom fields are chosen from the issue key's project prefix, using " +
                "profiles configured in the local init.ini (a common field set for every project, " +
                "plus one field set per configured project key, plus a default field set for any " +
                "other project). Each extra field is printed with its display name as returned by Jira. " +
                "Pass 'fields' explicitly to override this selection. " +
                "Automatically retries up to 10 times if the JIRA API returns an " +
                "invalid/incomplete response (a common symptom of flaky proxy/internet " +
                "connections). " +
                "Uses the JIRA REST API directly, authenticated via HTTP Basic Auth with an " +
                "API token.")]
        [Parameter(index: 0, name: "issue_key",
            type: "string",
            description: "JIRA issue key. Example: 'PROJ-123'",
            required: true)]
        [Parameter(index: 1, name: "fields",
            type: "string",
            description:
                "Comma-separated list of fields to retrieve (field ids, e.g. 'summary,customfield_12345'). " +
                "If omitted, the standard fields (summary, description, status, assignee, reporter, " +
                "comment, issuelinks, priority, labels, attachment) are retrieved PLUS the custom " +
                "fields configured in init.ini for the issue key's project. " +
                "Pass this parameter explicitly to override the init.ini selection.",
            required: false)]
        [Parameter(index: 2, name: "jira_base_url",
            type: "string",
            description: "JIRA instance base URL. Default: the Jira base URL configured in init.ini",
            required: false)]
        [Parameter(index: 3, name: "timeout_ms",
            type: "integer",
            description: "Request timeout in milliseconds. Default: 30000.",
            required: false)]
        public static object JiraReadTicket(JsonObject input)
        {
            string? issueKey  = input["issue_key"]?.GetValue<string>()?.Trim();
            string  baseUrl   = (input["jira_base_url"]?.GetValue<string>()
                                    ?? JiraDefaultBaseUrl).TrimEnd('/');
            int     timeoutMs = input["timeout_ms"]?.GetValue<int>() ?? 30_000;

            if (string.IsNullOrWhiteSpace(issueKey))
                return "Error: 'issue_key' is required.";

            string projectKey = JiraExtractProjectKey(issueKey);
            var (fields, sections) = JiraBuildFieldPlan(projectKey);

            string? fieldsOverride = input["fields"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(fieldsOverride))
            {
                fields = JiraSplitList(fieldsOverride);
                var extra = fields
                    .Where(f => !JiraCoreFields.Contains(f, StringComparer.OrdinalIgnoreCase))
                    .ToList();
                sections = new List<(string Title, List<string> Ids)>();
                if (extra.Count > 0)
                    sections.Add(("REQUESTED FIELDS", extra));
            }

            // Serialize request body ("expand: names" makes Jira return the field display names)
            string bodyJson = JsonSerializer.Serialize(
                new { jql = $"key = {issueKey}", fields, expand = "names" },
                JsonOptions);

            string url = $"{baseUrl}/rest/api/3/search/jql";

            var headers = new Dictionary<string, string>
            {
                { "Accept",       "application/json" },
                { "Content-Type", "application/json" }
            };

            // Fetch + parse + validate, retrying up to 10× on any invalid
            //    or incomplete response (see class-level remarks above) ─────
            var (success, outcome, lastError) = ToolsCommonPipe.RetryUntilValid(
                attempt: () => JiraTryFetchIssue(url, bodyJson, headers, timeoutMs, issueKey!, baseUrl, sections),
                isValid: r => r.IsValid,
                describeInvalid: r => r.Diagnostic,
                maxRetries: JiraTicketMaxRetries);

            if (!success)
                return $"No issue found with key '{issueKey}' in JIRA after {JiraTicketMaxRetries} attempt(s).\n" +
                       $"Last diagnostic: {lastError}";

            return outcome.Diagnostic; // on success, Diagnostic carries the fully formatted issue text
        }

        private static string JiraExtractProjectKey(string issueKey)
        {
            int dash = issueKey.IndexOf('-');
            return dash > 0 ? issueKey.Substring(0, dash) : issueKey;
        }

        private static (bool IsValid, string Diagnostic) JiraTryFetchIssue(
            string url, string bodyJson, Dictionary<string, string> headers,
            int timeoutMs, string issueKey, string baseUrl,
            List<(string Title, List<string> Ids)> sections)
        {
            string? responseBody = JiraGetResponseBody(
                new { command = "RestApiCall", url, httpMethod = "POST",
                      body = bodyJson, headers, timeoutMs });

            if (responseBody == null)
                return (false, "Error: No response body returned from JIRA API.");

            if (responseBody.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
                return (false, responseBody);

            try
            {
                using var doc  = JsonDocument.Parse(responseBody);
                JsonElement root = doc.RootElement;

                if (!root.TryGetProperty("issues", out var issuesArr) ||
                    issuesArr.ValueKind != JsonValueKind.Array)
                    return (false,
                        $"JIRA response for '{issueKey}' is missing a valid 'issues' array " +
                        $"(response was {responseBody.Length} char(s) — likely truncated by a " +
                        $"flaky proxy/internet connection).");

                if (issuesArr.GetArrayLength() == 0)
                    return (false,
                        $"JIRA returned zero issues for key '{issueKey}' " +
                        $"(response was {responseBody.Length} char(s)) — possibly a transient/" +
                        $"truncated response rather than a genuinely missing ticket.");

                JsonElement issue   = issuesArr[0];
                string returnedKey  = issue.TryGetProperty("key", out var kEl)  ? (kEl.GetString()  ?? issueKey) : issueKey;
                string issueId      = issue.TryGetProperty("id",  out var idEl) ? (idEl.GetString() ?? "")       : "";

                if (!issue.TryGetProperty("fields", out var f) || f.ValueKind != JsonValueKind.Object)
                    return (false, $"JIRA response for '{issueKey}' contains no valid 'fields' object.");

                JsonElement namesEl = default;
                bool hasNames = root.TryGetProperty("names", out namesEl) &&
                                namesEl.ValueKind == JsonValueKind.Object;

                return (true, JiraFormatIssue(f, namesEl, hasNames, sections, returnedKey, issueId, baseUrl));
            }
            catch (JsonException ex)
            {
                return (false,
                    $"Failed to parse JIRA API response — {ex.Message}\n\nRaw response:\n{responseBody}");
            }
        }

        private static string? JiraGetResponseBody(object requestObj)
        {
            string formatted = SendCommand(requestObj);

            const string marker = "--- API Response Body ---";
            int idx = formatted.IndexOf(marker, StringComparison.Ordinal);

            if (idx >= 0)
                return formatted.Substring(idx + marker.Length).TrimStart('\r', '\n').TrimEnd();

            if (formatted.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
                return formatted;

            return null;
        }

        private static string JiraGetOptionFieldValue(JsonElement fields, string fieldId)
        {
            if (!fields.TryGetProperty(fieldId, out var el))
                return "(not set)";

            string? text = JiraElementToText(el);
            return string.IsNullOrWhiteSpace(text) ? "(not set)" : text;
        }

        // Generic JSON -> text. Handles strings, numbers, booleans, option/user/issue objects,
        // cascading selects, ADF documents and arrays of those. Returns null when empty.
        private static string? JiraElementToText(JsonElement el)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.String:
                    return el.GetString();

                case JsonValueKind.Number:
                    return el.GetRawText();

                case JsonValueKind.True:
                    return "true";

                case JsonValueKind.False:
                    return "false";

                case JsonValueKind.Array:
                    var items = el.EnumerateArray()
                        .Select(JiraElementToText)
                        .Where(s => !string.IsNullOrEmpty(s))
                        .ToList();
                    return items.Count > 0 ? string.Join(", ", items) : null;

                case JsonValueKind.Object:
                    if (el.TryGetProperty("type", out var tEl) && tEl.ValueKind == JsonValueKind.String &&
                        string.Equals(tEl.GetString(), "doc", StringComparison.OrdinalIgnoreCase))
                        return JiraAdfToPlainText(el);

                    string? main = null;
                    foreach (string prop in new[] { "value", "name", "displayName" })
                    {
                        if (el.TryGetProperty(prop, out var pEl) && pEl.ValueKind == JsonValueKind.String)
                        {
                            main = pEl.GetString();
                            break;
                        }
                    }

                    // Issue reference (e.g. "parent"): key plus summary
                    if (main == null && el.TryGetProperty("key", out var kEl) && kEl.ValueKind == JsonValueKind.String)
                    {
                        main = kEl.GetString();
                        if (el.TryGetProperty("fields", out var sfEl) && sfEl.ValueKind == JsonValueKind.Object &&
                            sfEl.TryGetProperty("summary", out var ssEl) && ssEl.ValueKind == JsonValueKind.String &&
                            !string.IsNullOrWhiteSpace(ssEl.GetString()))
                            main += " — " + ssEl.GetString();
                    }

                    if (main == null) return "(unknown format)";

                    // Cascading select: append the child value
                    if (el.TryGetProperty("child", out var childEl) && childEl.ValueKind == JsonValueKind.Object)
                    {
                        string? childText = JiraElementToText(childEl);
                        if (!string.IsNullOrWhiteSpace(childText))
                            main += " / " + childText;
                    }
                    return main;

                default:
                    return null;
            }
        }

        // Format a parsed JIRA issue into a readable string.
        // 'sections' = (title, field ids) pairs to print after the core fields.
        // 'names'    = optional "names" map from the Jira response (field id -> display name).
        private static string JiraFormatIssue(
            JsonElement f, JsonElement names, bool hasNames,
            List<(string Title, List<string> Ids)> sections,
            string issueKey, string issueId, string baseUrl)
        {
            var sb = new StringBuilder();

            sb.AppendLine("╔══════════════════════════════════════════════════╗");
            sb.AppendLine($"  JIRA Ticket : {issueKey}  (id: {issueId})");
            sb.AppendLine($"  URL         : {baseUrl}/browse/{issueKey}");
            sb.AppendLine("╚══════════════════════════════════════════════════╝");
            sb.AppendLine();

            // Core fields
            string summary = f.TryGetProperty("summary", out var sumEl) ? (sumEl.GetString() ?? "") : "";
            sb.AppendLine($"Summary    : {summary}");

            string status = "";
            if (f.TryGetProperty("status", out var stEl) && stEl.ValueKind == JsonValueKind.Object &&
                stEl.TryGetProperty("name", out var stName))
                status = stName.GetString() ?? "";
            sb.AppendLine($"Status     : {status}");

            string priority = "";
            if (f.TryGetProperty("priority", out var priEl) && priEl.ValueKind == JsonValueKind.Object &&
                priEl.TryGetProperty("name", out var priName))
                priority = priName.GetString() ?? "";
            sb.AppendLine($"Priority   : {priority}");

            string assignee = "(Unassigned)";
            if (f.TryGetProperty("assignee", out var assEl) && assEl.ValueKind == JsonValueKind.Object &&
                assEl.TryGetProperty("displayName", out var assDn))
                assignee = assDn.GetString() ?? assignee;
            sb.AppendLine($"Assignee   : {assignee}");

            string reporter = "";
            if (f.TryGetProperty("reporter", out var repEl) && repEl.ValueKind == JsonValueKind.Object &&
                repEl.TryGetProperty("displayName", out var repDn))
                reporter = repDn.GetString() ?? "";
            sb.AppendLine($"Reporter   : {reporter}");

            // Labels
            if (f.TryGetProperty("labels", out var labsEl) && labsEl.ValueKind == JsonValueKind.Array)
            {
                var lblList = labsEl.EnumerateArray()
                    .Select(l => l.GetString())
                    .Where(l => !string.IsNullOrEmpty(l))
                    .ToList();
                sb.AppendLine($"Labels     : {(lblList.Count > 0 ? string.Join(", ", lblList) : "(none)")}");
            }

            // Field sections. Field ids and section titles come from Config\init.ini.
            // Field labels come from the "names" map of the Jira response (fallback: the field id).
            foreach (var (title, ids) in sections)
            {
                var rows = new List<(string Label, string Value)>();
                foreach (string id in ids)
                {
                    if (!f.TryGetProperty(id, out _)) continue;

                    string label = id;
                    if (hasNames && names.TryGetProperty(id, out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                        label = nameEl.GetString() ?? id;

                    rows.Add((label, JiraGetOptionFieldValue(f, id)));
                }
                if (rows.Count == 0) continue;

                int width = Math.Min(40, rows.Max(r => r.Label.Length));
                sb.AppendLine();
                sb.AppendLine($"┌── {title} ───────────────────────────────────────");
                foreach (var (label, value) in rows)
                {
                    string[] valueLines = value.Replace("\r\n", "\n").Split('\n');
                    sb.AppendLine($"│ {label.PadRight(width)} : {valueLines[0].TrimEnd()}");
                    for (int i = 1; i < valueLines.Length; i++)
                        sb.AppendLine($"│ {new string(' ', width)}   {valueLines[i].TrimEnd()}");
                }
                sb.AppendLine("└────────────────────────────────────────────────────────");
            }

            // Description
            sb.AppendLine();
            sb.AppendLine("┌── DESCRIPTION ──────────────────────────────────────────");
            if (f.TryGetProperty("description", out var descEl) &&
                descEl.ValueKind == JsonValueKind.Object)
            {
                string descText = JiraAdfToPlainText(descEl);
                foreach (string line in (string.IsNullOrWhiteSpace(descText) ? "(no description)" : descText)
                    .Split('\n'))
                    sb.AppendLine("│ " + line.TrimEnd());
            }
            else
            {
                sb.AppendLine("│ (no description)");
            }
            sb.AppendLine("└────────────────────────────────────────────────────────");

            // Attachments
            if (f.TryGetProperty("attachment", out var attachArr) &&
                attachArr.ValueKind == JsonValueKind.Array &&
                attachArr.GetArrayLength() > 0)
            {
                int attachCount = attachArr.GetArrayLength();
                sb.AppendLine();
                sb.AppendLine($"┌── ATTACHMENTS ({attachCount}) ─────────────────────────────────────");
                foreach (JsonElement att in attachArr.EnumerateArray())
                {
                    string attId       = att.TryGetProperty("id",       out var aId)   ? (aId.GetString()   ?? "") : "";
                    string attName     = att.TryGetProperty("filename",  out var aFn)   ? (aFn.GetString()   ?? "") : "";
                    string attMime     = att.TryGetProperty("mimeType",  out var aMime) ? (aMime.GetString() ?? "") : "";
                    string attContent  = att.TryGetProperty("content",   out var aCon)  ? (aCon.GetString()  ?? "") : "";
                    string attCreated  = att.TryGetProperty("created",   out var aCr)   ? (aCr.GetString()   ?? "") : "";
                    string attAuthor   = "";
                    if (att.TryGetProperty("author", out var aAuth) &&
                        aAuth.ValueKind == JsonValueKind.Object &&
                        aAuth.TryGetProperty("displayName", out var aDn))
                        attAuthor = aDn.GetString() ?? "";

                    long   attBytes    = att.TryGetProperty("size", out var aSz) ? aSz.GetInt64() : 0L;
                    string attSize     = attBytes >= 1_048_576
                        ? $"{attBytes / 1_048_576.0:F1} MB"
                        : attBytes >= 1_024
                            ? $"{attBytes / 1_024.0:F1} KB"
                            : $"{attBytes} B";

                    sb.AppendLine($"│  [{attId}]  {attName}");
                    sb.AppendLine($"│      Type    : {attMime}  ({attSize})");
                    sb.AppendLine($"│      By      : {attAuthor}  @  {attCreated}");
                    sb.AppendLine($"│      URL     : {attContent}");
                }
                sb.AppendLine("└────────────────────────────────────────────────────────");
            }

            // Linked Issues
            if (f.TryGetProperty("issuelinks", out var linksEl) &&
                linksEl.ValueKind == JsonValueKind.Array &&
                linksEl.GetArrayLength() > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"┌── LINKED ISSUES ({linksEl.GetArrayLength()}) ─────────────────────────────");
                foreach (JsonElement link in linksEl.EnumerateArray())
                {
                    string dirLabel = "";
                    if (link.TryGetProperty("type", out var lt))
                    {
                        bool isOutward = link.TryGetProperty("outwardIssue", out _);
                        dirLabel = isOutward
                            ? (lt.TryGetProperty("outward", out var ow) ? (ow.GetString() ?? "") : "outward")
                            : (lt.TryGetProperty("inward",  out var iw) ? (iw.GetString() ?? "") : "inward");
                    }

                    bool found =
                        link.TryGetProperty("outwardIssue", out JsonElement linkedIssue) ||
                        link.TryGetProperty("inwardIssue",  out linkedIssue);

                    if (!found || linkedIssue.ValueKind != JsonValueKind.Object) continue;

                    string lKey = linkedIssue.TryGetProperty("key", out var lkEl) ? (lkEl.GetString() ?? "") : "";
                    string lSum = "";
                    string lSts = "";
                    if (linkedIssue.TryGetProperty("fields", out var lf))
                    {
                        if (lf.TryGetProperty("summary", out var ls)) lSum = ls.GetString() ?? "";
                        if (lf.TryGetProperty("status",  out var lst) &&
                            lst.TryGetProperty("name",   out var lstn)) lSts = lstn.GetString() ?? "";
                    }

                    sb.AppendLine($"│  [{dirLabel}] {lKey}" +
                                  (string.IsNullOrEmpty(lSts) ? "" : $" [{lSts}]") +
                                  (string.IsNullOrEmpty(lSum) ? "" : $" — {lSum}"));
                }
                sb.AppendLine("└────────────────────────────────────────────────────────");
            }

            // Comments
            if (f.TryGetProperty("comment", out var commentObj) &&
                commentObj.TryGetProperty("comments", out var commentsArr) &&
                commentsArr.ValueKind == JsonValueKind.Array &&
                commentsArr.GetArrayLength() > 0)
            {
                int total = commentsArr.GetArrayLength();
                sb.AppendLine();
                sb.AppendLine($"┌── COMMENTS ({total}) ──────────────────────────────────────");

                int n = 1;
                foreach (JsonElement comment in commentsArr.EnumerateArray())
                {
                    string commentId = comment.TryGetProperty("id",      out var cId)  ? (cId.GetString()  ?? "") : "";
                    string author    = "";
                    string created   = comment.TryGetProperty("created", out var crEl) ? (crEl.GetString() ?? "") : "";

                    if (comment.TryGetProperty("author", out var authEl) &&
                        authEl.ValueKind == JsonValueKind.Object &&
                        authEl.TryGetProperty("displayName", out var dnEl))
                        author = dnEl.GetString() ?? "";

                    sb.AppendLine($"│");
                    sb.AppendLine($"│  ┌─ #{n} of {total} [id: {commentId}]");
                    sb.AppendLine($"│  │  Author  : {author}");
                    sb.AppendLine($"│  │  Created : {created}");
                    sb.AppendLine($"│  └─ Body:");

                    if (comment.TryGetProperty("body", out var bodyEl) &&
                        bodyEl.ValueKind == JsonValueKind.Object)
                    {
                        string commentText = JiraAdfToPlainText(bodyEl);
                        foreach (string line in (string.IsNullOrWhiteSpace(commentText) ? "(empty)" : commentText)
                            .Split('\n'))
                            sb.AppendLine("│     " + line.TrimEnd());
                    }
                    else
                    {
                        sb.AppendLine("│     (no body)");
                    }

                    n++;
                }
                sb.AppendLine("│");
                sb.AppendLine("└────────────────────────────────────────────────────────");
            }

            sb.AppendLine();
            sb.AppendLine($"[End of {issueKey}]");
            return sb.ToString().TrimEnd();
        }

        private static string JiraAdfToPlainText(JsonElement adfNode)
        {
            var sb = new StringBuilder();
            JiraWalkAdfNode(adfNode, sb, depth: 0, listCounter: null);
            return sb.ToString().TrimEnd('\n', '\r', ' ');
        }

        private static void JiraWalkAdfNode(
            JsonElement node, StringBuilder sb, int depth, int[]? listCounter)
        {
            if (node.ValueKind != JsonValueKind.Object) return;

            string type = node.TryGetProperty("type", out var nt) ? (nt.GetString() ?? "") : "";

            switch (type)
            {
                case "text":
                    string text = node.TryGetProperty("text", out var tv) ? (tv.GetString() ?? "") : "";
                    sb.Append(text);
                    break;

                case "hardBreak":
                    sb.AppendLine();
                    break;

                case "mention":
                    if (node.TryGetProperty("attrs", out var mAttr) &&
                        mAttr.TryGetProperty("text", out var mTxt))
                        sb.Append(mTxt.GetString());
                    break;

                case "inlineCard":
                    if (node.TryGetProperty("attrs", out var icAttr) &&
                        icAttr.TryGetProperty("url", out var icUrl))
                        sb.Append(icUrl.GetString());
                    break;

                case "rule":
                    sb.AppendLine("---");
                    break;

                case "paragraph":
                    JiraWalkChildren(node, sb, depth + 1, null);
                    sb.AppendLine();
                    break;

                case "heading":
                    int level = 2;
                    if (node.TryGetProperty("attrs", out var hAttrs) &&
                        hAttrs.TryGetProperty("level", out var hLvl))
                        level = hLvl.GetInt32();
                    sb.Append(new string('#', Math.Clamp(level, 1, 6)));
                    sb.Append(' ');
                    JiraWalkChildren(node, sb, depth + 1, null);
                    sb.AppendLine();
                    break;

                case "bulletList":
                    JiraWalkChildren(node, sb, depth + 1, null);
                    break;

                case "orderedList":
                    int startOrder = 1;
                    if (node.TryGetProperty("attrs", out var olAttrs) &&
                        olAttrs.TryGetProperty("order", out var olStart))
                        startOrder = olStart.GetInt32();
                    int[] counter = { startOrder };
                    JiraWalkChildren(node, sb, depth + 1, counter);
                    break;

                case "listItem":
                    string bullet = listCounter != null
                        ? $"{listCounter[0]++}. "
                        : "• ";
                    string indent = new string(' ', depth * 2);
                    sb.Append(indent + bullet);
                    JiraWalkChildren(node, sb, depth + 1, null);
                    break;

                case "codeBlock":
                    string lang = "";
                    if (node.TryGetProperty("attrs", out var cbAttrs) &&
                        cbAttrs.TryGetProperty("language", out var cbLang))
                        lang = cbLang.GetString() ?? "";
                    sb.AppendLine(string.IsNullOrEmpty(lang) ? "```" : $"```{lang}");
                    JiraWalkChildren(node, sb, depth + 1, null);
                    sb.AppendLine("```");
                    break;

                case "expand":
                    string expandTitle = "";
                    if (node.TryGetProperty("attrs", out var exAttrs) &&
                        exAttrs.TryGetProperty("title", out var exTitle))
                        expandTitle = exTitle.GetString() ?? "";
                    if (!string.IsNullOrWhiteSpace(expandTitle))
                        sb.AppendLine($"[{expandTitle}]");
                    JiraWalkChildren(node, sb, depth + 1, null);
                    break;

                case "media":
                    string mediaAltText  = "";
                    string mediaFileId   = "";
                    string mediaFileType = "";
                    if (node.TryGetProperty("attrs", out var mediaAttrs2))
                    {
                        if (mediaAttrs2.TryGetProperty("alt",  out var mAlt))  mediaAltText  = mAlt.GetString()  ?? "";
                        if (mediaAttrs2.TryGetProperty("id",   out var mId))   mediaFileId   = mId.GetString()   ?? "";
                        if (mediaAttrs2.TryGetProperty("type", out var mType)) mediaFileType = mType.GetString() ?? "";
                    }
                    if (string.IsNullOrWhiteSpace(mediaAltText) && string.IsNullOrWhiteSpace(mediaFileId))
                        sb.Append("[attachment]");
                    else if (string.IsNullOrWhiteSpace(mediaAltText))
                        sb.Append($"[{mediaFileType}: id={mediaFileId}]");
                    else if (string.IsNullOrWhiteSpace(mediaFileId))
                        sb.Append($"[{mediaFileType}: {mediaAltText}]");
                    else
                        sb.Append($"[{mediaFileType}: {mediaAltText} (media-id={mediaFileId})]");
                    break;

                case "mediaSingle":
                    JiraWalkChildren(node, sb, depth + 1, null);
                    sb.AppendLine();
                    break;

                default:
                    // doc, tableRow, tableCell, and any unknown container
                    JiraWalkChildren(node, sb, depth, listCounter);
                    break;
            }
        }

        private static void JiraWalkChildren(
            JsonElement node, StringBuilder sb, int depth, int[]? listCounter)
        {
            if (!node.TryGetProperty("content", out var content)) return;
            if (content.ValueKind != JsonValueKind.Array) return;
            foreach (JsonElement child in content.EnumerateArray())
                JiraWalkAdfNode(child, sb, depth, listCounter);
        }
    }
}
