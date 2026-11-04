using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace GTA.Tools
{
    internal static partial class ToolsJiraApi
    {
        [ToolTag("jira_api")]
        [ToolInfo(
            name: "jira_write_comment",
            description: "Post a formatted comment to a JIRA issue. " +
                         "Accepts simple Markdown-like syntax and converts it to " +
                         "Atlassian Document Format (ADF) internally — no need to construct ADF JSON manually. " +
                         "Uses the JIRA REST API directly via HTTP Basic Auth with an API token.\n\n" +
                         "Supported Markdown syntax:\n" +
                         "  ## Text              — Heading level 2\n" +
                         "  ### Text             — Heading level 3\n" +
                         "  **text**             — Bold\n" +
                         "  *text*               — Italic\n" +
                         "  `code`               — Inline code\n" +
                         "  ```lang\\n...\\n```  — Fenced code block (language tag is optional)\n" +
                         "  @[Name](accountId)   — User mention — triggers JIRA notification\n" +
                         "  ---                  — Horizontal divider\n" +
                         "  blank line           — Paragraph break\n\n" +
                         "IMPORTANT: The accountId in @[Name](accountId) mentions must be the JIRA " +
                         "user account ID (UUID or numeric string), NOT the display name or email. " +
                         "Obtain accountIds from issue fields such as reporter.accountId, " +
                         "assignee.accountId, or comment author accountIds returned by the JIRA REST API.")]
        [Parameter(index: 0, name: "issue_key",
            type: "string",
            description: "JIRA issue key. Example: 'PROJ-123'",
            required: true)]
        [Parameter(index: 1, name: "body",
            type: "string",
            description: "Comment body using Markdown-like syntax. See tool description for full syntax reference.",
            required: true)]
        [Parameter(index: 2, name: "jira_base_url",
            type: "string",
            description: "JIRA instance base URL. Default: the Jira base URL configured in init.ini",
            required: false)]
        [Parameter(index: 3, name: "timeout_ms",
            type: "integer",
            description: "Request timeout in milliseconds. Default: 30000.",
            required: false)]
        public static object JiraWriteComment(JsonObject input)
        {
            string? issueKey  = input["issue_key"]?.GetValue<string>();
            string? bodyMd    = input["body"]?.GetValue<string>();
            string  baseUrl   = (input["jira_base_url"]?.GetValue<string>()
                                    ?? JiraDefaultBaseUrl).TrimEnd('/');
            int     timeoutMs = input["timeout_ms"]?.GetValue<int>() ?? 30_000;

            if (string.IsNullOrWhiteSpace(issueKey)) return "Error: 'issue_key' is required.";
            if (string.IsNullOrWhiteSpace(bodyMd))   return "Error: 'body' is required.";

            JsonObject adfDoc      = JiraAdfFromMarkdown(bodyMd);
            string     requestBody = "{\"body\":" + adfDoc.ToJsonString() + "}";

            string url = $"{baseUrl}/rest/api/3/issue/{issueKey.Trim()}/comment";

            var headers = new Dictionary<string, string>
            {
                { "Accept",       "application/json" },
                { "Content-Type", "application/json" }
            };

            return SendCommand(new { command = "RestApiCall", url, httpMethod = "POST",
                                     body = requestBody, headers, timeoutMs });
        }

        // Block parser: Markdown string → ADF doc node
        private static JsonObject JiraAdfFromMarkdown(string markdown)
        {
            var nodes  = new JsonArray();
            string[] lines = markdown.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            int i = 0;

            while (i < lines.Length)
            {
                string raw     = lines[i];
                string trimmed = raw.Trim();

                // Heading: ###
                if (trimmed.StartsWith("### ", StringComparison.Ordinal))
                {
                    nodes.Add(JiraAdfHeading(trimmed.Substring(4).Trim(), 3));
                    i++; continue;
                }
                // Heading: ##
                if (trimmed.StartsWith("## ", StringComparison.Ordinal))
                {
                    nodes.Add(JiraAdfHeading(trimmed.Substring(3).Trim(), 2));
                    i++; continue;
                }

                // Horizontal rule
                if (trimmed == "---" || trimmed == "***" || trimmed == "___")
                {
                    nodes.Add(new JsonObject { ["type"] = "rule" });
                    i++; continue;
                }

                // Fenced code block
                if (trimmed.StartsWith("```", StringComparison.Ordinal))
                {
                    string lang = trimmed.Substring(3).Trim();
                    var codeLines = new List<string>();
                    i++;
                    while (i < lines.Length &&
                           !lines[i].Trim().StartsWith("```", StringComparison.Ordinal))
                    {
                        codeLines.Add(lines[i]);
                        i++;
                    }
                    if (i < lines.Length) i++; // consume closing ```
                    nodes.Add(JiraAdfCodeBlock(string.Join("\n", codeLines), lang));
                    continue;
                }

                // Blank line — skip
                if (string.IsNullOrWhiteSpace(trimmed)) { i++; continue; }

                // Paragraph — accumulate consecutive non-special lines
                var paraLines = new List<string>();
                while (i < lines.Length)
                {
                    string t = lines[i].Trim();
                    if (string.IsNullOrWhiteSpace(t))                               break;
                    if (t.StartsWith("### ", StringComparison.Ordinal))             break;
                    if (t.StartsWith("## ",  StringComparison.Ordinal))             break;
                    if (t.StartsWith("```",  StringComparison.Ordinal))             break;
                    if (t == "---" || t == "***" || t == "___")                     break;
                    paraLines.Add(lines[i]);
                    i++;
                }

                if (paraLines.Count == 0) continue;

                var paraContent = new JsonArray();
                for (int j = 0; j < paraLines.Count; j++)
                {
                    if (j > 0)
                        paraContent.Add(new JsonObject { ["type"] = "hardBreak" });
                    JiraAdfInlineNodes(paraLines[j], paraContent);
                }
                nodes.Add(new JsonObject { ["type"] = "paragraph", ["content"] = paraContent });
            }

            return new JsonObject { ["type"] = "doc", ["version"] = 1, ["content"] = nodes };
        }

        private static void JiraAdfInlineNodes(string text, JsonArray nodes)
        {
            int pos = 0;
            while (pos < text.Length)
            {
                char ch = text[pos];

                // @[Display Name](accountId) — mention node
                if (ch == '@' && pos + 1 < text.Length && text[pos + 1] == '[')
                {
                    int nameEnd = text.IndexOf(']', pos + 2);
                    if (nameEnd > 0 && nameEnd + 1 < text.Length && text[nameEnd + 1] == '(')
                    {
                        int idEnd = text.IndexOf(')', nameEnd + 2);
                        if (idEnd > 0)
                        {
                            nodes.Add(new JsonObject
                            {
                                ["type"]  = "mention",
                                ["attrs"] = new JsonObject
                                {
                                    ["id"]          = text.Substring(nameEnd + 2, idEnd - nameEnd - 2),
                                    ["text"]        = "@" + text.Substring(pos + 2, nameEnd - pos - 2),
                                    ["accessLevel"] = ""
                                }
                            });
                            pos = idEnd + 1;
                            continue;
                        }
                    }
                }

                // `inline code` — single mark: code
                if (ch == '`')
                {
                    int end = text.IndexOf('`', pos + 1);
                    if (end > pos)
                    {
                        nodes.Add(new JsonObject
                        {
                            ["type"]  = "text",
                            ["text"]  = text.Substring(pos + 1, end - pos - 1),
                            ["marks"] = new JsonArray { new JsonObject { ["type"] = "code" } }
                        });
                        pos = end + 1;
                        continue;
                    }
                }

                // **bold** — single mark: strong
                if (ch == '*' && pos + 1 < text.Length && text[pos + 1] == '*')
                {
                    int end = text.IndexOf("**", pos + 2, StringComparison.Ordinal);
                    if (end > pos + 1)
                    {
                        nodes.Add(new JsonObject
                        {
                            ["type"]  = "text",
                            ["text"]  = text.Substring(pos + 2, end - pos - 2),
                            ["marks"] = new JsonArray { new JsonObject { ["type"] = "strong" } }
                        });
                        pos = end + 2;
                        continue;
                    }
                }

                // *italic* — single mark: em
                if (ch == '*')
                {
                    int end = text.IndexOf('*', pos + 1);
                    if (end > pos)
                    {
                        nodes.Add(new JsonObject
                        {
                            ["type"]  = "text",
                            ["text"]  = text.Substring(pos + 1, end - pos - 1),
                            ["marks"] = new JsonArray { new JsonObject { ["type"] = "em" } }
                        });
                        pos = end + 1;
                        continue;
                    }
                }

                // Plain text — advance until next special character
                int next = pos + 1;
                while (next < text.Length)
                {
                    char c2 = text[next];
                    if (c2 == '@' || c2 == '`' || c2 == '*') break;
                    next++;
                }
                nodes.Add(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = text.Substring(pos, next - pos)
                });
                pos = next;
            }
        }

        private static JsonObject JiraAdfHeading(string text, int level)
            => new JsonObject
            {
                ["type"]    = "heading",
                ["attrs"]   = new JsonObject { ["level"] = level },
                ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } }
            };

        private static JsonObject JiraAdfCodeBlock(string code, string? language)
            => new JsonObject
            {
                ["type"]    = "codeBlock",
                ["attrs"]   = new JsonObject { ["language"] = language ?? "" },
                ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = code } }
            };
    }
}
