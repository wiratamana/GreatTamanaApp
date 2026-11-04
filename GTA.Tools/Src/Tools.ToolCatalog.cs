using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace GTA.Tools
{
    internal static class ToolCatalog
    {
        private sealed class Category
        {
            public string Name { get; }
            public string Description { get; }
            public Func<string, bool> Matches { get; }

            public Category(string name, string description, Func<string, bool> matches)
            {
                Name        = name;
                Description = description;
                Matches     = matches;
            }
        }

        private static bool HasAnyTag(string toolName, params string[] tags)
        {
            string[] toolTags = ToolRegistry.GetTagsForTool(toolName);
            if (toolTags.Length == 0) return false;
            foreach (string t in tags)
                if (toolTags.Contains(t, StringComparer.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        private static readonly Category[] _categories =
        {
            new Category(
                "io",
                "File & directory operations: read/write/edit/search files, browse & manage directories, zip/unzip archives.",
                name => HasAnyTag(name, "essential", "io-specialist", "directory", "archive")),

#if !CO_WORKER_USE
            new Category(
                "dotnet",
                "Build, run, and scaffold .NET console projects; enumerate C# source files.",
                name => name.StartsWith("dotnet_", StringComparison.OrdinalIgnoreCase)),

            new Category(
                "git",
                "Local git repository operations via a bundled portable git: init/status/config, " +
                "stage/commit/rm/mv, branch/switch/merge/rebase, restore/reset/revert/clean, stash, and " +
                "log/diff/show/tag (offline). Also GitHub-facing ONLINE operations (proxy/Wi-Fi guarded): " +
                "push/pull/fetch/clone, create a remote repository, create a pull request, and GitHub " +
                "Issues (list/read/comment/open-close). Every operation takes a 'repository_path' pointing " +
                "at a local folder.",
                name => HasAnyTag(name, "git")),
#endif

            new Category(
                "box",
                "Box.com cloud storage: browse folders, search, download/read docx, xlsx, pptx files and embedded images.",
                name => HasAnyTag(name, "box_api")),

            new Category(
                "jira",
                "JIRA issue tracker: read tickets, read attachments (docx/xlsx/pptx/images/video), post comments.",
                name => HasAnyTag(name, "jira_api")),

            new Category(
                "xlsx",
                "Edit a LOCAL .xlsx file on disk: query sheets, read/write cell values (incl. " +
                "formulas), create/rename sheets, merge/unmerge ranges. Loads from and saves back " +
                "to a local Windows file path (not Box/Jira).",
                name => HasAnyTag(name, "xlsx_local")),

            new Category(
                "pptx",
                "Build/edit a LOCAL .pptx file on disk: create a new presentation, add slides, " +
                "draw shapes (rectangle/rounded rectangle/diamond/block arrow), connectors (plain/" +
                "arrow/glued-by-name/looping elbow), tables, images, text boxes, bulleted/numbered " +
                "lists, and render slides to PNG for visual verification. Loads from and saves back " +
                "to a local Windows file path (not Box/Jira).",
                name => HasAnyTag(name, "pptx_local")),

            new Category(
                "gte",
                "Great Tamana Engine network bridge: send an HTTP request to a locally-running " +
                "GreatTamanaEngine instance (e.g. capture the current rendered frame via " +
                "/get_swapchain or /get_game_view) and view the result directly as an image.",
                name => name.StartsWith("gte_", StringComparison.OrdinalIgnoreCase)),

            new Category(
                "ibmcloud",
                "IBM Cloud Logs: keyword/thread search, match-count stats, automated root-cause trace investigation, evidence export.",
                name => HasAnyTag(name, "ibm_cloud_api")),

#if !CO_WORKER_USE
            new Category(
                "avalonia",
                "Control the local Avalonia desktop chat UI: launch/close, screenshots, send messages, switch agents.",
                name => HasAnyTag(name, "avalonia")),
#endif

#if CO_WORKER_USE
            new Category(
                "external",
                "Load an additional local image file for visual inspection, and http_request " +
                "(in-process HTTP calls to the local editor server at 127.0.0.1).",
                name => HasAnyTag(name, "image", "http")),
#else
            new Category(
                "external",
                "Run raw Windows shell commands, compile C/C++ with gcc/g++, run cmake, load an " +
                "additional local image file for visual inspection, or launch/stop a local app in " +
                "the BACKGROUND (run_app_background / stop_app_background) so you can keep " +
                "interacting with it (network calls, screenshots, health checks) while it runs. " +
                "Also: http_request (in-process HTTP calls to the local editor server at 127.0.0.1 — use " +
                "this instead of PowerShell Invoke-RestMethod/curl) and inline_python (run a Python " +
                "snippet given as a string).",
                name => HasAnyTag(name, "shell", "image", "gcc", "cmake", "process", "http", "python")),
#endif

            new Category(
                "encyclopedia",
                "Persistent on-disk knowledge base: write free-text entries (encyclopedia_write) " +
                "and query all of them via Gemini gemini-3.5-flash for precise, fact-only answers " +
                "(encyclopedia_read). NOT part of the default agent's bootstrap toolset — load it " +
                "on demand via load_tools when you need to write/query the knowledge base.",
                name => HasAnyTag(name, "encyclopedia")),

            new Category(
                "bug_report",
                "Persistent on-disk bug log for TOOL MALFUNCTIONS ONLY: record a tool that errored, " +
                "timed out, or returned a wrong/unexpected result while you were actually using it " +
                "(bug_report). Not for general ideas/suggestions. Nothing reads it back automatically " +
                "— it is reviewed by a human later. Already part of the default agent's bootstrap toolset.",
                name => HasAnyTag(name, "bug_report")),
        };

        private static HashSet<string> ActiveToolNames()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            JsonArray? active = ToolRegistry.ActiveTools;
            if (active == null) return set;

            foreach (JsonNode? entry in active)
            {
                string? name = entry?["name"]?.GetValue<string>();
                if (name != null && !ToolRegistry.IsHidden(name)) set.Add(name);
            }
            return set;
        }

        // ── get_available_tools ────────────────────────────────────────────────

        [ToolInfo(
            name: "get_available_tools",
            description:
                "Discover tools that exist but are NOT yet loaded into this conversation's active tool set. " +
#if CO_WORKER_USE
                "Call with NO arguments first to see the high-level CATEGORY overview (io, box, jira, ibmcloud, " +
                "external). Then call again WITH a 'category' argument to see the " +
#else
                "Call with NO arguments first to see the high-level CATEGORY overview (io, git, box, jira, ibmcloud, " +
                "dotnet, avalonia, external). Then call again WITH a 'category' argument to see the " +
#endif
                "individual tool names (and short descriptions) inside that one category — so you know exactly " +
                "what exact name(s) to pass to load_tools. This tool never modifies your active tool set; it only " +
                "reports what is available.")]
        [Parameter(index: 0, name: "category",
            type: "string",
            description: "Optional. Exact category name (case-insensitive), e.g. 'box' or 'jira'. " +
                         "Omit (or leave empty) to see the list of all categories instead of one category's tools.",
            required: false)]
        public static object GetAvailableTools(JsonObject input)
        {
            string? category = input["category"]?.GetValue<string>()?.Trim();
            HashSet<string> active = ActiveToolNames();

            if (string.IsNullOrEmpty(category))
            {
                var sb = new StringBuilder();
                sb.AppendLine("Available tool categories (call get_available_tools again with a 'category' " +
                              "argument to list the individual tools inside one):");
                sb.AppendLine();

                foreach (Category cat in _categories)
                {
                    List<string> names  = ToolRegistry.Tools.Keys
                        .Where(n => cat.Matches(n) && !ToolRegistry.IsHidden(n)).ToList();
                    int          total  = names.Count;
                    int          loaded = names.Count(active.Contains);
                    sb.AppendLine($"  {cat.Name,-11} ({loaded}/{total} already active) — {cat.Description}");
                }

                sb.AppendLine();
                sb.AppendLine(active.Count > 0
                    ? "Currently active tools in this conversation: " + string.Join(", ", active.OrderBy(n => n))
                    : "No tools are currently marked active (this should not normally happen).");
                return sb.ToString();
            }

            Category? match = _categories.FirstOrDefault(
                c => string.Equals(c.Name, category, StringComparison.OrdinalIgnoreCase));

            if (match == null)
                return $"Error: Unknown category '{category}'. Valid categories: " +
                       string.Join(", ", _categories.Select(c => c.Name)) + ".";

            List<string> toolNames = ToolRegistry.Tools.Keys
                .Where(n => match.Matches(n) && !ToolRegistry.IsHidden(n)).OrderBy(n => n).ToList();
            if (toolNames.Count == 0)
                return $"Category '{match.Name}' has no registered tools (unexpected).";

            var result = new StringBuilder();
            result.AppendLine($"Tools in category '{match.Name}' — {match.Description}");
            result.AppendLine();

            foreach (string name in toolNames)
            {
                JsonObject? entry = ToolRegistry.TryGetSchemaEntry(name);
                string      desc  = entry?["description"]?.GetValue<string>() ?? "";
                if (desc.Length > 160) desc = desc[..160] + "…";
                string mark = active.Contains(name) ? "  [already active]" : "";
                result.AppendLine($"  - {name}{mark}: {desc}");
            }

            result.AppendLine();
            string? example = toolNames.FirstOrDefault(n => !active.Contains(n));
            if (example != null)
                result.AppendLine($"To load one or more, call: load_tools(tool_names: \"{example}\")");

            return result.ToString();
        }

        // ── load_tools ──────────────────────────────────────────────────────────

        [ToolInfo(
            name: "load_tools",
            description:
                "Load one or more additional tools into THIS conversation's active tool set, by exact name. " +
                "Use get_available_tools first to discover valid tool names. Supports loading multiple tools in " +
                "a single call via a comma-separated list, e.g. 'jira_read_xlsx,jira_read_pptx'. Every tool loaded " +
                "this way becomes callable starting on your VERY NEXT tool call — no restart or extra round-trip " +
                "needed.")]
        [Parameter(index: 0, name: "tool_names",
            type: "string",
            description: "Comma-separated exact tool name(s) to load, e.g. 'box_get_folder' or " +
                         "'jira_read_xlsx,jira_read_pptx'.",
            required: true)]
        public static object LoadTools(JsonObject input)
        {
            string? raw = input["tool_names"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(raw))
                return "Error: 'tool_names' parameter is missing or empty.";

            JsonArray? active = ToolRegistry.ActiveTools;
            if (active == null)
                return "Error: No active tool-schema session was found for this call. " +
                       "load_tools must be invoked from within an AgenticLoop session " +
                       "(ToolRegistry.ActiveTools was never set).";

            HashSet<string> alreadyActive = ActiveToolNames();

            var loaded   = new List<string>();
            var skipped  = new List<string>();
            var notFound = new List<string>();

            foreach (string rawName in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                string name = rawName.Trim();
                if (name.Length == 0) continue;

                if (alreadyActive.Contains(name))
                {
                    skipped.Add(name);
                    continue;
                }

                if (ToolRegistry.IsHidden(name))
                {
                    notFound.Add(name);
                    continue;
                }

                JsonObject? entry = ToolRegistry.TryGetSchemaEntry(name);
                if (entry == null)
                {
                    notFound.Add(name);
                    continue;
                }

                active.Add(entry);
                alreadyActive.Add(name);
                loaded.Add(name);
            }

            var sb = new StringBuilder();

            if (loaded.Count > 0)
                sb.AppendLine($"\u2714 Loaded ({loaded.Count}): {string.Join(", ", loaded)}");

            if (skipped.Count > 0)
                sb.AppendLine($"\u2014 Already active, skipped ({skipped.Count}): {string.Join(", ", skipped)}");

            if (notFound.Count > 0)
            {
                sb.AppendLine($"\u2718 Unknown tool name(s) ({notFound.Count}): {string.Join(", ", notFound)}");
                sb.AppendLine("  Call get_available_tools(category: \"<name>\") to see valid exact tool names.");
            }

            if (sb.Length == 0)
                sb.AppendLine("No valid tool names were provided.");

            sb.AppendLine();
            sb.Append($"Active tool count is now: {active.Count}");

            return sb.ToString();
        }
    }
}
